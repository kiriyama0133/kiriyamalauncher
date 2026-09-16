using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>一条网络接口（虚拟网卡）的描述，用于跨平台统一表示。</summary>
/// <param name="Name">接口名/别名（Windows：InterfaceAlias，如「ZeroTier One [bb40b36408000001]」；Linux/macOS：如 ztbb40b36408、ztabc1234）。</param>
/// <param name="DisplayName">给用户看的名称（Windows 的 Description，其它平台通常等于 Name）。</param>
/// <param name="Metric">当前路由度量值（metric，越小优先级越高）；拿不到时为 null。</param>
public readonly record struct NetworkInterfaceInfo(string Name, string DisplayName, int? Metric);

/// <summary>网卡优先级（metric）调整结果。</summary>
/// <param name="IsSuccess">是否成功。</param>
/// <param name="InterfaceName">命中的网卡名（未命中时为空）。</param>
/// <param name="Message">给界面显示的说明。</param>
public readonly record struct RouteMetricResult(bool IsSuccess, string InterfaceName, string Message);

/// <summary>
/// 跨平台「虚拟网卡优先级」服务：自动发现 ZeroTier 虚拟网卡并把它的路由度量（metric）
/// 调到最高优先级（数值最小），让游戏流量优先走隧道。
///
/// 与硬编码网卡名不同，这里按「网卡特征」自动发现：
///   - Windows：枚举网卡，按别名/描述含 <c>ZeroTier</c> 关键字匹配；
///   - Linux：枚举网卡，按 <c>zt</c> 前缀（ZeroTier 官方命名）匹配；
///   - macOS：枚举硬件端口，按端口名含 <c>ZeroTier</c> 匹配。
///
/// 每个平台一个实现（<see cref="WindowsRouteMetricService"/> /
/// <see cref="LinuxRouteMetricService"/> / <see cref="MacRouteMetricService"/>），
/// 由 DI 工厂按 <see cref="OperatingSystem"/> 选择，不支持平台回退
/// <see cref="UnsupportedRouteMetricService"/> —— 与 <see cref="IFirewallService"/> 同一套模式。
/// </summary>
public interface IRouteMetricService
{
    /// <summary>当前平台是否支持自动调整网卡 metric。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 自动发现 ZeroTier 虚拟网卡，并把它的 metric 调到指定值。
    /// </summary>
    /// <param name="networkId">
    /// 当前加入的 ZeroTier 网络 ID（16 位十六进制）。用于在多个候选网卡里精确命中
    /// （ZeroTier 网卡名通常带网络 ID 的低 16 位十六进制），传 0 时退化为纯关键字匹配。
    /// </param>
    /// <param name="metric">目标 metric（越小优先级越高，一般传 1）。</param>
    Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default);
}
