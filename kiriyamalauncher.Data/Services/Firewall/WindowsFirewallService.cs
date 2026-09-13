using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// Windows 实现：直接调用系统自带的 netsh advfirewall，不引入任何 Windows 专用 NuGet 包。
/// 需要管理员权限；没有权限时返回失败说明，由界面提示用户手动放行。
/// </summary>
public class WindowsFirewallService : IFirewallService
{
    private readonly ILogger<WindowsFirewallService> _logger;

    public WindowsFirewallService(ILogger<WindowsFirewallService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public Task<FirewallResult> AllowUdpInboundAsync(string ruleName, int udpPort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        return RunNetshAsync(
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={ruleName}",
                "dir=in",
                "action=allow",
                "protocol=UDP",
                $"localport={udpPort}"
            ],
            $"已放行 UDP {udpPort} 入站（规则：{ruleName}）。",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FirewallResult> RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleName);

        return RunNetshAsync(
            ["advfirewall", "firewall", "delete", "rule", $"name={ruleName}"],
            $"已删除防火墙规则「{ruleName}」。",
            cancellationToken);
    }

    private async Task<FirewallResult> RunNetshAsync(IReadOnlyList<string> arguments, string successMessage, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("netsh")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new FirewallResult(false, "无法启动 netsh，请手动放行端口。");
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("防火墙规则已更新：netsh {Arguments}", string.Join(' ', arguments));
                return new FirewallResult(true, successMessage);
            }

            string message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            _logger.LogWarning("netsh 执行失败（退出码 {ExitCode}）：{Message}", process.ExitCode, message);

            return new FirewallResult(false, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "调用 netsh 失败。");
            return new FirewallResult(false, $"调用 netsh 失败：{ex.Message}");
        }
    }
}
