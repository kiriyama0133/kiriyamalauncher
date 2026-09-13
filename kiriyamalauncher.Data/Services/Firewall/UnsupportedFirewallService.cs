using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 非 Windows 平台的默认实现：不改动系统防火墙，只返回手动放行的提示。
/// （Linux 的 ufw / firewalld、macOS 的 pf 都需要提权且各有差异，交给用户自己处理更安全。）
/// </summary>
public class UnsupportedFirewallService : IFirewallService
{
    private readonly ILogger<UnsupportedFirewallService>? _logger;

    public UnsupportedFirewallService(ILogger<UnsupportedFirewallService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<FirewallResult> AllowUdpInboundAsync(string ruleName, int udpPort, CancellationToken cancellationToken = default)
    {
        string hint = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? $"请在「系统设置 → 网络 → 防火墙」中允许本应用接受传入连接（UDP {udpPort}）。"
            : $"请手动放行 UDP {udpPort}，例如：sudo ufw allow {udpPort}/udp";

        _logger?.LogInformation("当前平台不支持自动配置防火墙，返回手动提示：{Hint}", hint);

        return Task.FromResult(new FirewallResult(false, hint));
    }

    /// <inheritdoc />
    public Task<FirewallResult> RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default)
        => Task.FromResult(new FirewallResult(false, "当前平台不需要自动删除防火墙规则。"));
}
