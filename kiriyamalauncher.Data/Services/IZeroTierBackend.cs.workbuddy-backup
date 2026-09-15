using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 一个具体的 ZeroTier 传输后端实现。
///
/// 目前只有一种实现 <see cref="ZeroTierService"/>（基于 ZeroTier.Sockets / libzt 内嵌节点）。
/// 未来接入「嵌入的 ZeroTier 客户端」（走系统虚拟网卡）时，再新增一个实现并注册进来，
/// <see cref="ZeroTierBackendFactory"/> 会根据用户偏好的传输引擎选择对应后端。
/// </summary>
public interface IZeroTierBackend : IZeroTierService
{
    /// <summary>
    /// 后端类型标识，对应 <see cref="Entities.ZeroTierSettings"/> 里的常量：
    /// Sockets / Client。
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// 应用退出时的清理（尽力而为，绝不抛出、绝不长时间阻塞退出）。
    /// 客户端引擎：离开全部已加入的网络（虚拟网卡随 leave 一并移除）并停止系统服务，
    /// 避免「启动器关了，ZeroTier 服务和虚拟网卡还常驻后台」；
    /// libzt 引擎是进程内实现，随进程退出，本方法为空操作。
    /// </summary>
    Task CleanupOnExitAsync();
}
