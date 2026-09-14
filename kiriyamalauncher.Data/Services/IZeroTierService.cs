using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>ZeroTier 节点状态快照。</summary>
public record ZeroTierStatus(bool IsStarted, string NodeId, ulong NetworkId, string VirtualIp, bool IsOnline, bool IsTransportReady, string StatusText);

/// <summary>虚拟局域网 ping 的结果。</summary>
/// <param name="IsSuccess">是否收到对端响应。</param>
/// <param name="RoundTripMilliseconds">往返耗时（毫秒）。</param>
/// <param name="Message">给界面显示的说明。</param>
public record ZeroTierPingResult(bool IsSuccess, long RoundTripMilliseconds, string Message);

/// <summary>
/// ZeroTier 服务：管理内嵌节点（libzt）的启动、加入网络、moon 轨道、以及虚拟 IP / 连接状态查询。
/// </summary>
public interface IZeroTierService
{
    /// <summary>虚拟局域网 Ping 使用的 TCP 端口（双方都运行本程序时可以互 ping）。</summary>
    const int PingPort = 43333;

    /// <summary>节点事件（节点上线、网络就绪等），用于向界面报告进度。</summary>
    event EventHandler<string>? EventRaised;

    /// <summary>节点是否已经启动。</summary>
    bool IsStarted { get; }

    /// <summary>本机在虚拟局域网里的 IP（没有接入网络时为空）。</summary>
    string LocalVirtualIp { get; }

    /// <summary>本机虚拟网段前缀（例如 "10.74.203."），扫描/发现设备时用。</summary>
    string LocalNetworkPrefix { get; }

    /// <summary>
    /// 目前仍然停留在哪些网络里。
    ///
    /// libzt 1.8.4 不支持退网（调用即原生崩溃），所以断开只是「应用层断开」：
    /// 换 Network ID 之后节点会同时停留在旧网络和新网络里，界面用这个信息提示用户。
    /// </summary>
    IReadOnlyList<ulong> JoinedNetworkIds { get; }

    /// <summary>启动节点（使用 storagePath 保存节点身份）。</summary>
    Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default);

    /// <summary>让节点围绕指定的 moon 运行（moonId 为十六进制字符串，例如 1947244ee4）。</summary>
    Task OrbitMoonAsync(ulong moonId, CancellationToken cancellationToken = default);

    /// <summary>加入网络并等待虚拟 IP。</summary>
    Task<ZeroTierStatus> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在虚拟局域网里 ping 一台主机。
    ///
    /// 走的是 libzt 自己的 TCP 连接探测（不是系统的 ping 命令），所以不需要虚拟网卡、
    /// 也不受 Windows 防火墙对 ICMP 的限制；代价是对端必须在目标端口上有监听。
    /// 本程序会在 <see cref="PingPort"/> 上监听，所以两台都开着本程序时可以直接互 ping。
    /// </summary>
    Task<ZeroTierPingResult> PingAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// 断开虚拟局域网（应用层断开，不调用 libzt 的退网接口）。
    ///
    /// 实测 libzt 1.8.4 的 zts_net_leave 会让进程在原生层直接崩溃（日志停在退网调用上，
    /// 既没有返回码也没有异常），所以这里故意不碰原生层：节点保持在线，
    /// 只是本程序不再使用这个网络，之后 <see cref="JoinNetworkAsync"/> 会立刻连回来。
    /// </summary>
    Task<ZeroTierStatus> DisconnectAsync(ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>读取当前状态（不改变任何东西）。</summary>
    ZeroTierStatus GetStatus(ulong networkId);

    /// <summary>停止并释放节点（只在应用退出时调用；之后本进程内不能再次启动节点）。</summary>
    void Stop();
}
