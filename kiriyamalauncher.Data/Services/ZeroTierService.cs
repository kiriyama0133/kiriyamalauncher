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

    /// <summary>已经加入过的网络（libzt 无法退网，这里留个记录用于提示用户）。</summary>
    private readonly List<ulong> _joinedNetworkIds = [];

    /// <summary>节点是否已经被释放过（libzt 同进程内不能重新初始化）。</summary>
    private bool _nodeFreed;

    /// <summary>虚拟局域网 Ping 的响应器（在虚拟网络上监听一个 TCP 端口）。</summary>
    private ZeroTier.Sockets.Socket? _pingListener;
    private CancellationTokenSource? _pingListenerSource;

    public ZeroTierService(ILogger<ZeroTierService> logger)
    {
        _logger = logger;
    }

    public bool IsStarted => _node is not null;

    /// <inheritdoc />
    public IReadOnlyList<ulong> JoinedNetworkIds => _joinedNetworkIds.ToArray();

    /// <inheritdoc />
    public string LocalVirtualIp
    {
        get
        {
            if (_node is null || _networkId == 0)
            {
                return string.Empty;
            }

            return GetStatus(_networkId).VirtualIp;
        }
    }

    /// <inheritdoc />
    public string LocalNetworkPrefix
    {
        get
        {
            ZeroTier.Core.Node? node = _node;

            if (node is null || _networkId == 0)
            {
                return string.Empty;
            }

            string? subnet = null;

            try
            {
                // 优先用网络下发的路由（例如 10.74.203.0/24）推网段前缀。
                subnet = node.Networks?
                    .FirstOrDefault(network => network.Id == _networkId)?
                    .Routes?
                    .Select(route => route.Target)
                    .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
                    .ToString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取网络路由失败，改用本机虚拟 IP 推网段。");
            }

            subnet ??= LocalVirtualIp;

            if (string.IsNullOrWhiteSpace(subnet))
            {
                return string.Empty;
            }

            string[] parts = subnet.Split('.');
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}." : string.Empty;
        }
    }

    public async Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        await _nodeLock.WaitAsync(cancellationToken);

        try
        {
            if (_node is not null)
            {
                return GetStatus(_networkId);
            }

            if (_nodeFreed)
            {
                // libzt 的节点在一个进程里只能初始化一次：销毁过之后必须重启应用。
                throw new InvalidOperationException("内嵌 ZeroTier 节点已在本次运行中关闭，libzt 不支持重新初始化，请重启应用。");
            }

            Directory.CreateDirectory(storagePath);

            ZeroTier.Core.Node node = new();
            node.InitFromStorage(storagePath);
            node.InitAllowNetworkCaching(true);
            node.InitAllowPeerCaching(true);
            node.InitSetEventHandler(OnZeroTierEvent);
            // 注意避开 Windows 的 UDP 保留段（Hyper-V / WSL / Docker 的 winnat 会预留一些区间，
            // 例如 50000-50059），否则 libzt 的节点端口可能 bind 失败。
            node.InitSetRandomPortRange(41000, 49000);

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
        ZeroTier.Core.Node? node = _node;

        if (node is null)
        {
            throw new InvalidOperationException("ZeroTier 节点还没有启动。");
        }

        _networkId = networkId;

        // 同样是原生调用，放到线程池上避免阻塞界面线程。
        int joinResult = await Task.Run(() => node.Join(networkId), cancellationToken);
        _logger.LogInformation("正在加入网络 {NetworkId:x}……（返回码 {Code}）", networkId, joinResult);

        if (joinResult == LIBZT_SUCCESS && !_joinedNetworkIds.Contains(networkId))
        {
            _joinedNetworkIds.Add(networkId);

            if (_joinedNetworkIds.Count > 1)
            {
                // 退网接口在这个 libzt 版本里会崩，所以换网络时旧网络会一直留着，记录下来提醒用户。
                _logger.LogWarning(
                    "节点同时停留在 {Count} 个网络里（libzt 无法退网）：{Networks}",
                    _joinedNetworkIds.Count,
                    string.Join(", ", _joinedNetworkIds.Select(id => id.ToString("x16"))));
            }
        }

        // 等到拿到虚拟地址（或超时）。
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < JOIN_TIMEOUT)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.IsNetworkTransportReady(networkId) && node.GetNetworkAddresses(networkId).Count > 0)
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        ZeroTierStatus status = GetStatus(networkId);

        // 网络就绪后开一个 Ping 响应器：别的机器可以直接 ping 本机的虚拟 IP。
        if (status.IsTransportReady)
        {
            StartPingResponder();
        }

        return status;
    }

    public ZeroTierStatus GetStatus(ulong networkId)
    {
        ZeroTier.Core.Node? node = _node;

        if (node is null)
        {
            return new ZeroTierStatus(false, string.Empty, networkId, string.Empty, false, false, "节点未启动");
        }

        if (networkId == 0)
        {
            // 没有具体网络可查：不要拿 0 去调用原生接口（libzt 对非法网络的容错很差）。
            return new ZeroTierStatus(true, node.IdString, 0, string.Empty, node.Online, false, "未加入网络");
        }

        List<IPAddress> addresses = node.GetNetworkAddresses(networkId) ?? [];
        IPAddress? virtualAddress = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        bool transportReady = node.IsNetworkTransportReady(networkId);

        int status = node.Networks?.FirstOrDefault(n => n.Id == networkId)?.Status
            ?? ZeroTier.Constants.NETWORK_STATUS_REQUESTING_CONFIGURATION;

        return new ZeroTierStatus(
            true,
            node.IdString,
            networkId,
            virtualAddress?.ToString() ?? string.Empty,
            node.Online,
            transportReady,
            DescribeNetworkStatus(status, transportReady));
    }

    public void Stop()
    {
        StopPingResponder();

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
            _nodeFreed = true;
        }
    }

    /// <summary>
    /// 断开虚拟局域网：应用层断开，不调用 libzt 的退网接口。
    ///
    /// 原因：libzt 1.8.4 的 zts_net_leave 会让进程在原生层直接崩溃 ——
    /// 日志实测停在「正在退出网络 …」之后就没有任何输出，托管层既拿不到返回码也拿不到异常，
    /// AppDomain / Dispatcher 的崩溃处理器都来不及执行。所以这里只清掉「当前网络」的记录，
    /// 节点本身继续在线，重连时再 Join 同一网络即可（几秒内拿回虚拟 IP）。
    /// </summary>
    public Task<ZeroTierStatus> DisconnectAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("已在应用层断开网络 {NetworkId:x}（不调用 libzt 退网，节点保持在线）。", networkId);

        if (_networkId == networkId)
        {
            _networkId = 0;
        }

        StopPingResponder();

        return Task.FromResult(GetStatus(networkId));
    }

    /// <inheritdoc />
    public async Task<ZeroTierPingResult> PingAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_node is null)
        {
            return new ZeroTierPingResult(false, 0, "ZeroTier 节点还没有启动，请先连接到虚拟局域网。");
        }

        if (string.IsNullOrWhiteSpace(host) || !IPAddress.TryParse(host.Trim(), out IPAddress? address))
        {
            return new ZeroTierPingResult(false, 0, $"「{host}」不是合法的 IP 地址。");
        }

        int connectTimeout = (int)Math.Clamp(timeout.TotalMilliseconds, 100d, 30000d);

        return await Task.Run(() =>
        {
            ZeroTier.Sockets.Socket? socket = null;

            try
            {
                socket = new ZeroTier.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp)
                {
                    ConnectTimeout = connectTimeout
                };

                Stopwatch stopwatch = Stopwatch.StartNew();
                socket.Connect(new IPEndPoint(address, port));
                stopwatch.Stop();

                return new ZeroTierPingResult(true, stopwatch.ElapsedMilliseconds, $"来自 {address} 的响应：时间 {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Ping {Host}:{Port} 无响应：{Message}", host, port, ex.Message);
                return new ZeroTierPingResult(false, 0, $"请求无响应（{ex.Message}）");
            }
            finally
            {
                try
                {
                    socket?.Close();
                }
                catch (Exception)
                {
                    // 关闭失败不影响结果。
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 在虚拟网络上监听一个 TCP 端口，接受连接后立刻关闭 ——
    /// 这样别的机器用 <see cref="PingAsync"/> 就能探测到本机（不需要任何虚拟网卡）。
    /// </summary>
    private void StartPingResponder()
    {
        if (_pingListener is not null)
        {
            return;
        }

        try
        {
            ZeroTier.Sockets.Socket listener = new(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);

            listener.Bind(new IPEndPoint(IPAddress.Any, IZeroTierService.PingPort));
            listener.Listen(8);

            CancellationTokenSource source = new();
            _pingListener = listener;
            _pingListenerSource = source;

            _ = Task.Run(() => AcceptPingLoopAsync(listener, source.Token));

            _logger.LogInformation("虚拟局域网 Ping 响应器已启动（TCP {Port}）。", IZeroTierService.PingPort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动虚拟局域网 Ping 响应器失败（端口 {Port}）。", IZeroTierService.PingPort);
        }
    }

    private async Task AcceptPingLoopAsync(ZeroTier.Sockets.Socket listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ZeroTier.Sockets.Socket client = listener.Accept();
                client.Close();
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Ping 响应器结束。");
                }

                break;
            }
        }
    }

    private void StopPingResponder()
    {
        _pingListenerSource?.Cancel();
        _pingListenerSource?.Dispose();
        _pingListenerSource = null;

        try
        {
            _pingListener?.Close();
        }
        catch (Exception)
        {
            // 关闭失败无所谓。
        }

        _pingListener = null;
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
