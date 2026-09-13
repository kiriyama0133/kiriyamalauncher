using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>防火墙操作结果（是否成功 + 可以直接显示给用户的说明）。</summary>
/// <param name="IsSuccess">是否执行成功。</param>
/// <param name="Message">结果说明。</param>
public record FirewallResult(bool IsSuccess, string Message);

/// <summary>
/// 跨平台防火墙服务：联机前把内嵌 ZeroTier 需要的 UDP 端口放行。
///
/// 这里刻意不再依赖 Windows 专用的第三方防火墙包：
/// Windows 走系统自带的 netsh（需要管理员权限），
/// 其它平台由 <see cref="UnsupportedFirewallService"/> 返回手动放行提示，
/// 这样整个解决方案可以跨平台编译并做 NativeAOT 发布。
/// </summary>
public interface IFirewallService
{
    /// <summary>当前平台是否支持自动放行。</summary>
    bool IsSupported { get; }

    /// <summary>放行 UDP 入站端口。</summary>
    Task<FirewallResult> AllowUdpInboundAsync(string ruleName, int udpPort, CancellationToken cancellationToken = default);

    /// <summary>删除之前添加的规则（断开联机 / 退出应用时调用）。</summary>
    Task<FirewallResult> RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default);
}
