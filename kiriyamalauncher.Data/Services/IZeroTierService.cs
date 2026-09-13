using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>ZeroTier 节点状态快照。</summary>
public record ZeroTierStatus(bool IsStarted, string NodeId, ulong NetworkId, string VirtualIp, bool IsOnline, bool IsTransportReady, string StatusText);

/// <summary>
/// ZeroTier 服务：管理内嵌节点（libzt）的启动、加入网络、moon 轨道、以及虚拟 IP / 连接状态查询。
/// </summary>
public interface IZeroTierService
{
    /// <summary>节点事件（节点上线、网络就绪等），用于向界面报告进度。</summary>
    event EventHandler<string>? EventRaised;

    /// <summary>节点是否已经启动。</summary>
    bool IsStarted { get; }

    /// <summary>启动节点（使用 storagePath 保存节点身份）。</summary>
    Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default);

    /// <summary>让节点围绕指定的 moon 运行（moonId 为十六进制字符串，例如 1947244ee4）。</summary>
    Task OrbitMoonAsync(ulong moonId, CancellationToken cancellationToken = default);

    /// <summary>加入网络并等待虚拟 IP。</summary>
    Task<ZeroTierStatus> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>读取当前状态（不改变任何东西）。</summary>
    ZeroTierStatus GetStatus(ulong networkId);

    /// <summary>停止并释放节点。</summary>
    void Stop();
}
