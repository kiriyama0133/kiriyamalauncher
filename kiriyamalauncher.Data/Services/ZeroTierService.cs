using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 基于 ZeroTier.Sockets（libzt）内嵌节点的实现：
/// 不需要安装 ZeroTier 客户端，也不需要虚拟网卡驱动。
/// </summary>
public class ZeroTierService : IZeroTierService, IDisposable
{
    /// <summary>libzt 的成功返回码。</summary>
    private const int LIBZT_SUCCESS = 0;

    /// <summary>等待节点上线的最长时间。</summary>
    private static readonly TimeSpan NODE_ONLINE_TIMEOUT = TimeSpan.FromSeconds(20);

    /// <summary>等待加入网络（拿到虚拟 IP）的最长时间。</summary>
    private static readonly TimeSpan JOIN_TIMEOUT = TimeSpan.FromSeconds(30);

    /// <summary>libzt 里的 moon 轨道接口（托管包装没有公开，这里直接 P/Invoke）。</summary>
    [DllImport("libzt", EntryPoint = "CSharp_zts_moon_orbit")]
    private static extern int zts_moon_orbit(ulong moonId, ulong seed);

    private readonly ILogger<ZeroTierService> _logger;
    private readonly SemaphoreSlim _nodeLock = new(1, 1);

    public event EventHandler<string>? EventRaised;

    private ZeroTier.Core.Node? _node;
    private ulong _networkId;

    public ZeroTierService(ILogger<ZeroTierService> logger)
    {
        _logger = logger;
    }

    public bool IsStarted => _node is not null;

    public async Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        await _nodeLock.WaitAsync(cancellationToken);

        try
        {
            if (_node is not null)
            {
                return GetStatus(_networkId);
            }

            Directory.CreateDirectory(storagePath);

            ZeroTier.Core.Node node = new();
            node.InitFromStorage(storagePath);
            node.InitAllowNetworkCaching(true);
            node.InitAllowPeerCaching(true);
            node.InitSetEventHandler(OnZeroTierEvent);
            node.InitSetRandomPortRange(40000, 50000);

            int startResult = node.Start();
            if (startResult != LIBZT_SUCCESS)
            {
                _logger.LogError("ZeroTier 节点启动失败，返回码 {Code}。", startResult);
                throw new InvalidOperationException($"ZeroTier 节点启动失败（{startResult}）。");
            }

            _node = node;
            _logger.LogInformation("ZeroTier 节点已启动，节点 ID：{NodeId}", node.IdString);

            await WaitUntilAsync(() => node.Online, NODE_ONLINE_TIMEOUT, cancellationToken);

            return GetStatus(_networkId);
        }
        finally
        {
            _nodeLock.Release();
        }
    }

    public async Task OrbitMoonAsync(ulong moonId, CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            throw new InvalidOperationException("ZeroTier 节点还没有启动。");
        }

        await Task.Yield();

        int result = zts_moon_orbit(moonId, moonId);
        if (result == LIBZT_SUCCESS)
        {
            _logger.LogInformation("已围绕 moon {MoonId:x} 建立轨道。", moonId);
        }
        else
        {
            _logger.LogWarning("moon 轨道设置失败，返回码 {Code}。", result);
            throw new InvalidOperationException($"moon 轨道设置失败（{result}）。");
        }

        await Task.CompletedTask;
    }

    public async Task<ZeroTierStatus> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            throw new InvalidOperationException("ZeroTier 节点还没有启动。");
        }

        _networkId = networkId;
        _node.Join(networkId);
        _logger.LogInformation("正在加入网络 {NetworkId:x}……", networkId);

        // 等到拿到虚拟地址（或超时）。
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < JOIN_TIMEOUT)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_node.IsNetworkTransportReady(networkId) && _node.GetNetworkAddresses(networkId).Count > 0)
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        return GetStatus(networkId);
    }

    public ZeroTierStatus GetStatus(ulong networkId)
    {
        if (_node is null)
        {
            return new ZeroTierStatus(false, string.Empty, networkId, string.Empty, false, false, "节点未启动");
        }

        List<IPAddress> addresses = _node.GetNetworkAddresses(networkId) ?? [];
        IPAddress? virtualAddress = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        bool transportReady = _node.IsNetworkTransportReady(networkId);

        int status = _node.Networks?.FirstOrDefault(n => n.Id == networkId)?.Status
            ?? ZeroTier.Constants.NETWORK_STATUS_REQUESTING_CONFIGURATION;

        return new ZeroTierStatus(
            true,
            _node.IdString,
            networkId,
            virtualAddress?.ToString() ?? string.Empty,
            _node.Online,
            transportReady,
            DescribeNetworkStatus(status, transportReady));
    }

    public void Stop()
    {
        if (_node is null)
        {
            return;
        }

        try
        {
            _node.Free();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 ZeroTier 节点时出错。");
        }
        finally
        {
            _node = null;
            _networkId = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        _nodeLock.Dispose();
    }

    private static string DescribeNetworkStatus(int status, bool transportReady)
    {
        if (transportReady)
        {
            return "已连接";
        }

        // 注意：libzt 的状态码是 static readonly（不是 const），只能用 if 比较。
        if (status == ZeroTier.Constants.NETWORK_STATUS_OK)
        {
            return "网络已就绪，等待地址";
        }

        if (status == ZeroTier.Constants.NETWORK_STATUS_ACCESS_DENIED)
        {
            return "被网络拒绝（未授权）";
        }

        if (status == ZeroTier.Constants.NETWORK_STATUS_NOT_FOUND)
        {
            return "网络不存在";
        }

        if (status == ZeroTier.Constants.NETWORK_STATUS_PORT_ERROR)
        {
            return "端口错误";
        }

        if (status == ZeroTier.Constants.NETWORK_STATUS_CLIENT_TOO_OLD)
        {
            return "客户端版本过旧";
        }

        return status == ZeroTier.Constants.NETWORK_STATUS_REQUESTING_CONFIGURATION
            ? "正在等待网络配置"
            : $"未知状态（{status}）";
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (!condition() && stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(200, cancellationToken);
        }
    }

    private void OnZeroTierEvent(ZeroTier.Core.Event e)
    {
        _logger.LogDebug("ZeroTier 事件：{Code} {Name}", e.Code, e.Name);

        string? message = e.Code switch
        {
            201 => "节点已上线",
            213 => $"网络配置已就绪：{e.NetworkInfo?.Id:x16}",
            _ => null
        };

        if (message is not null)
        {
            EventRaised?.Invoke(this, message);
        }
    }
}
