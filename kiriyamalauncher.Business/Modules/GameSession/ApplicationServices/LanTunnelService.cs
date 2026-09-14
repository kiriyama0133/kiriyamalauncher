using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using kiriyamalauncher.Data;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// <see cref="ILanTunnelService"/> 的默认实现：包一层数据层的隧道，负责状态与日志。
/// </summary>
public class LanTunnelService : ILanTunnelService
{
    private readonly IVirtualLanTunnel _tunnel;
    private readonly ILogger<LanTunnelService> _logger;

    private string _statusText = "隧道未启动。";

    public LanTunnelService(IVirtualLanTunnel tunnel, ILogger<LanTunnelService> logger)
    {
        _tunnel = tunnel;
        _logger = logger;
        _tunnel.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public bool IsRunning => _tunnel.IsRunning;

    /// <inheritdoc />
    public bool IsClientConnected => _tunnel.IsClientConnected;

    /// <inheritdoc />
    public string PeerVirtualIp => _tunnel.PeerVirtualIp;

    /// <inheritdoc />
    public IReadOnlyList<string> PeerVirtualIps => _tunnel.PeerVirtualIps;

    /// <inheritdoc />
    public string StatusText => _statusText;

    /// <inheritdoc />
    public event EventHandler<string>? StatusChanged;

    /// <inheritdoc />
    public Task<GameOperationResultDto> StartAsync(string localVirtualIp, IReadOnlyList<string> peerVirtualIps,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GameOperationResultDto.Fail("游戏隧道目前只支持 Windows。"));
        }

        try
        {
            _tunnel.Start(localVirtualIp, peerVirtualIps);

            string peers = peerVirtualIps is { Count: > 0 } ? string.Join("、", peerVirtualIps) : "（还没发现设备）";

            _logger.LogInformation("游戏隧道启动：本机 {Local}，对端 {Peers}", localVirtualIp, peers);

            return Task.FromResult(GameOperationResultDto.Ok($"隧道已就绪：对端 {peers}，等待游戏侧 Hook 连接。"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动游戏隧道失败。");

            // 管道只有 1 个服务端实例，被占用几乎只有一个原因：已经有另一个启动器在跑。
            // 这不是能"重试成功"的错误，说清楚比抛原始系统消息有用。
            string message = ex is System.IO.IOException
                ? "启动隧道失败：游戏隧道已被占用 —— 多半是已经有一个启动器在运行，请只保留一个窗口。"
                : $"启动隧道失败：{ex.Message}";

            return Task.FromResult(GameOperationResultDto.Fail(message));
        }
    }

    /// <inheritdoc />
    public void UpdatePeers(IReadOnlyList<string> peerVirtualIps)
    {
        try
        {
            _tunnel.UpdatePeers(peerVirtualIps);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新隧道对端列表失败。");
        }
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        try
        {
            _tunnel.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止游戏隧道时出错。");
        }

        return Task.CompletedTask;
    }

    private void OnStateChanged(object? sender, string message)
    {
        _statusText = message;
        StatusChanged?.Invoke(this, message);
    }
}
