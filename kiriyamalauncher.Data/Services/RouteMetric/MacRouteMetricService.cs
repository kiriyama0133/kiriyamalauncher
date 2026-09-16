using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// macOS 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由度量（metric）。
///
/// 说明：macOS 没有 Linux/Windows 那样直观的「接口 metric」概念，路由优先级主要由
/// 路由表的度量值决定。这里通过 <c>ifconfig</c> 枚举网卡（ZeroTier One 在 macOS 上
/// 同样用 <c>zt</c> 前缀），并用 <c>ifconfig &lt;iface&gt; metric &lt;n&gt;</c> 尝试设置；
/// 若系统不支持该语法（取决于 ZeroTier 虚拟接口实现），返回明确提示让用户用
/// <c>route -n add -interface &lt;iface&gt;</c> 手动指定。
/// </summary>
public sealed class MacRouteMetricService : IRouteMetricService
{
    private const string ZEROTIER_IFACE_PREFIX = "zt";

    private readonly ILogger<MacRouteMetricService> _logger;

    public MacRouteMetricService(ILogger<MacRouteMetricService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NetworkInterfaceInfo> interfaces = await EnumerateInterfacesAsync(cancellationToken).ConfigureAwait(false);

        NetworkInterfaceInfo? target = PickZeroTierInterface(interfaces, networkId);

        if (target is null)
        {
            string hint = interfaces.Count == 0
                ? "未发现任何网络接口（ifconfig 枚举失败）。"
                : "未发现 ZeroTier 网卡（zt 前缀），可能尚未接入网络。";
            _logger.LogWarning("未命中 ZeroTier 网卡（候选 {Count} 块）：{Hint}", interfaces.Count, hint);
            return new RouteMetricResult(false, string.Empty, hint);
        }

        ProcessRunResult result = await RunProcessAsync(
            "ifconfig",
            [target.Value.Name, "metric", metric.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」metric 设为 {Metric}。", target.Value.Name, metric);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」的优先级调到最高（metric={metric}）。");
        }

        // macOS 的 ZeroTier 虚拟接口不一定支持 metric 参数，给出可执行的兜底提示。
        string message = $"自动调整「{target.Value.Name}」metric 失败（{result.ErrorText}）。macOS 建议手动指定路由：sudo route -n add -interface {target.Value.Name}";
        return new RouteMetricResult(false, target.Value.Name, message);
    }

    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "ifconfig",
            ["-l"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return ParseIfconfigList(result.Output);
    }

    /// <summary>ifconfig -l 输出为单行空格分隔的网卡名列表。</summary>
    private static List<NetworkInterfaceInfo> ParseIfconfigList(string output)
    {
        var results = new List<NetworkInterfaceInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string name in output.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            results.Add(new NetworkInterfaceInfo(name.Trim(), name.Trim(), null));
        }

        return results;
    }

    private static NetworkInterfaceInfo? PickZeroTierInterface(IReadOnlyList<NetworkInterfaceInfo> interfaces, ulong networkId)
    {
        if (interfaces.Count == 0)
        {
            return null;
        }

        string networkIdHex = networkId == 0
            ? string.Empty
            : networkId.ToString("x16", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(networkIdHex))
        {
            NetworkInterfaceInfo? exact = interfaces.FirstOrDefault(
                i => IsZeroTierInterface(i) && i.Name.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                return exact;
            }
        }

        return interfaces.FirstOrDefault(IsZeroTierInterface);
    }

    private static bool IsZeroTierInterface(NetworkInterfaceInfo i)
        => i.Name.StartsWith(ZEROTIER_IFACE_PREFIX, StringComparison.OrdinalIgnoreCase);

    private async Task<ProcessRunResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
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
                return new ProcessRunResult(false, -1, string.Empty, $"无法启动 {executable}。");
            }

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return new ProcessRunResult(false, -1, string.Empty, $"命令超时（{timeout.TotalSeconds:N0} 秒）");
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return new ProcessRunResult(true, 0, output, string.Empty);
            }

            string message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            return new ProcessRunResult(false, process.ExitCode, output, string.IsNullOrWhiteSpace(message) ? $"命令失败（退出码 {process.ExitCode}）" : message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "运行 {Exe} 失败。", executable);
            return new ProcessRunResult(false, -1, string.Empty, ex.Message);
        }
    }

    private readonly record struct ProcessRunResult(bool IsSuccess, int ExitCode, string Output, string ErrorText);
}
