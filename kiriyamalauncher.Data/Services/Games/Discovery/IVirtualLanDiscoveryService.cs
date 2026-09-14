using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 虚拟局域网设备发现：在虚拟网段里轮询，找出同一个网络里在线的设备。
///
/// 主用方式：用 libzt 的 UDP socket 往广播地址发探测包，收到应答的设备就是在线设备
/// （对方也运行本程序时，它的发现服务会自动应答）。广播不可用时自动回退成 TCP 扫描。
/// 换成虚拟网卡（系统网络栈）之后这里可以直接换成系统 UDP 广播，接口不用动。
/// </summary>
public interface IVirtualLanDiscoveryService
{
    /// <summary>UDP 发现使用的端口（双方都运行本程序时互发现用）。</summary>
    const int DiscoveryPort = 43334;

    /// <summary>发现新设备。</summary>
    event EventHandler<VirtualLanPeer>? PeerDiscovered;

    /// <summary>设备离线（连续多次探测不到）。</summary>
    event EventHandler<VirtualLanPeer>? PeerLost;

    /// <summary>一轮扫描结束（带上当前所有在线设备）。可能在后台线程触发。</summary>
    event EventHandler<IReadOnlyList<VirtualLanPeer>>? ScanCompleted;

    /// <summary>「是否正在轮询」状态变化（开始 / 停止）。可能在后台线程触发。</summary>
    event EventHandler? ScanningChanged;

    /// <summary>当前在线的设备。</summary>
    IReadOnlyList<VirtualLanPeer> Peers { get; }

    /// <summary>是否正在周期轮询。</summary>
    bool IsScanning { get; }

    /// <summary>本机的虚拟 IP。</summary>
    string LocalVirtualIp { get; }

    /// <summary>本机虚拟网段前缀（例如 "10.74.203."）。</summary>
    string LocalNetworkPrefix { get; }

    /// <summary>当前用的发现方式（给界面提示用）。</summary>
    string TransportDescription { get; }

    /// <summary>扫描一次。</summary>
    /// <param name="ipPrefix">要扫描的网段前缀，空则用本机网段。</param>
    /// <param name="probePort">TCP 回退扫描时探测的端口。</param>
    Task<IReadOnlyList<VirtualLanPeer>> ScanOnceAsync(
        string? ipPrefix = null,
        int probePort = IZeroTierService.PingPort,
        CancellationToken cancellationToken = default);

    /// <summary>开始周期轮询。</summary>
    void StartScanning(TimeSpan interval, string? ipPrefix = null, int probePort = IZeroTierService.PingPort);

    /// <summary>停止轮询。</summary>
    void StopScanning();

    /// <summary>清空已发现的设备。</summary>
    void Clear();
}
