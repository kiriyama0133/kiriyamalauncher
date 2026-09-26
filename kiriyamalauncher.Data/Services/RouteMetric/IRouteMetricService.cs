using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>一条网络接口（虚拟网卡）的描述，用于跨平台统一表示。</summary>
/// <param name="Name">接口名/别名（Windows：InterfaceAlias，如「ZeroTier One [bb40b36408000001]」；Linux/macOS：如 ztbb40b36408、ztabc1234）。</param>
/// <param name="DisplayName">给用户看的名称（Windows 的 Description，其它平台通常等于 Name）。</param>
/// <param name="Metric">当前路由度量值（metric，越小优先级越高）；拿不到时为 null。</param>
/// <param name="IfIndex">
/// 系统接口索引（Windows 的 ifIndex、Linux/macOS 的 link index）；拿不到时为 null。
/// 下发设置时**优先用索引而不是名字**：Windows 的别名里含方括号
/// （如 <c>ZeroTier One [bb40b36408000001]</c>），而 PowerShell 的 -InterfaceAlias 支持通配符、
/// 会把 <c>[...]</c> 当字符集解析，结果是「静默匹配 0 个对象、不报错、退出码 0」的假成功；
/// 索引是纯数字，天然规避该问题。
/// </param>
/// <param name="IsVirtual">
/// 系统是否把它识别为**虚拟网卡**（Windows 的 <c>Get-NetAdapter.Virtual</c>、Linux 的
/// 「<c>/sys/class/net/&lt;name&gt;/device</c> 不存在」）；拿不到时为 null，
/// 此时由 <see cref="RouteMetricPolicy.IsVirtualInterface"/> 按名称关键字兜底判断。
/// 判定「虚拟」是为了**只动虚拟网卡**：实体网卡承载用户正常的网络出口，改它会牵连非联机流量。
/// </param>
public readonly record struct NetworkInterfaceInfo(string Name, string DisplayName, int? Metric, int? IfIndex = null, bool? IsVirtual = null);

/// <summary>网卡优先级（metric）调整结果。</summary>
/// <param name="IsSuccess">是否成功。</param>
/// <param name="InterfaceName">命中的网卡名（未命中时为空）。</param>
/// <param name="Message">给界面显示的说明（已包含「下调了哪些并列网卡」的信息）。</param>
/// <param name="DemotedInterfaces">
/// 为消除并列而被下调优先级的其它虚拟网卡名（无并列时为空/null）。
/// 这些网卡**不属于我们**，其原 metric 已记入日志，便于用户手动还原。
/// </param>
public readonly record struct RouteMetricResult(
    bool IsSuccess,
    string InterfaceName,
    string Message,
    IReadOnlyList<string>? DemotedInterfaces = null);

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
    /// 自动发现 ZeroTier 虚拟网卡，把它的 metric 调到指定值，并**消除并列**：
    /// 若本机另有虚拟网卡同样处于最高优先级（metric ≤ 目标值），会把它们下调一档，
    /// 否则并列时由系统内部规则决定次序，我们的网卡可能反而排在后面
    /// （表现为「日志说成功，流量仍不走隧道」）。实体网卡不会被改动。
    /// </summary>
    /// <param name="networkId">
    /// 当前加入的 ZeroTier 网络 ID（16 位十六进制）。用于在多个候选网卡里精确命中
    /// （ZeroTier 网卡名通常带网络 ID 的低 16 位十六进制），传 0 时退化为纯关键字匹配。
    /// </param>
    /// <param name="metric">目标 metric（越小优先级越高，一般传 1）。</param>
    Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default);
}
