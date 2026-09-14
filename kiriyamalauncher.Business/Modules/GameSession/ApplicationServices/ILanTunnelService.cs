using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// 游戏隧道服务：把注入游戏里的 Hook 送来的 UDP 报文，经内嵌 ZeroTier 单播给对端虚拟 IP，
/// 反向把对端数据交回 Hook（由 Hook 喂给游戏）。
/// </summary>
public interface ILanTunnelService
{
    /// <summary>隧道是否已启动。</summary>
    bool IsRunning { get; }

    /// <summary>游戏侧 Hook 是否已连上。</summary>
    bool IsClientConnected { get; }

    /// <summary>对端虚拟 IP。</summary>
    string PeerVirtualIp { get; }

    /// <summary>所有对端虚拟 IP。</summary>
    IReadOnlyList<string> PeerVirtualIps { get; }

    /// <summary>状态文本（给界面显示）。</summary>
    string StatusText { get; }

    /// <summary>状态变化（可能在后台线程触发，界面需要自己切回 UI 线程）。</summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// 启动隧道。<paramref name="localVirtualIp"/> 是本机虚拟 IP，
    /// <paramref name="peerVirtualIps"/> 是要互通的对端（可以多台）。
    /// </summary>
    Task<GameOperationResultDto> StartAsync(string localVirtualIp, IReadOnlyList<string> peerVirtualIps,
        CancellationToken cancellationToken = default);

    /// <summary>运行中更新对端列表（设备上下线）。</summary>
    void UpdatePeers(IReadOnlyList<string> peerVirtualIps);

    /// <summary>停止隧道。</summary>
    Task StopAsync();
}
