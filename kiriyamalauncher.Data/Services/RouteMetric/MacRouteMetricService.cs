using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// macOS 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由优先级。
///
/// 说明：macOS 源于 BSD，其 <c>ifconfig</c> **支持** <c>metric</c> 参数
/// （<c>ifconfig &lt;iface&gt; metric &lt;n&gt;</c>，这点和 Linux 的 iproute2 不同——
/// 后者没有该参数），所以这里以 ifconfig 为首选；若某些版本不接受该参数，则降级为
/// <c>route -n add -interface &lt;iface&gt;</c> 指定该接口承载路由。
///
/// 设置后**读回校验**：重新读取 <c>ifconfig &lt;iface&gt;</c> 的 metric 并与目标值比对。
/// 若该系统版本的 ifconfig 压根不回报 metric，则如实标注「未能校验」，不谎报成功。
/// </summary>
public sealed partial class MacRouteMetricService : IRouteMetricService
{
    private const string ZEROTIER_IFACE_PREFIX = "zt";

    private readonly ILogger<MacRouteMetricService> _logger;

    public MacRouteMetricService(ILogger<MacRouteMetricService> logger)
    {
        _logger = logger;
    }

    /// <summary>从 ifconfig 输出里取 metric（源生成正则，NativeAOT 裁剪下安全）。</summary>
    [GeneratedRegex(@"\bmetric\s+(?<metric>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IfconfigMetricRegex();

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

        string iface = target.Value.Name;
        string metricText = metric.ToString(CultureInfo.InvariantCulture);

        // 首选：BSD 的 ifconfig 支持 metric 参数。
        ProcessRunResult ifconfig = await RunProcessAsync(
            "ifconfig",
            [iface, "metric", metricText],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        int? actual = await ReadMetricAsync(iface, cancellationToken).ConfigureAwait(false);

        if (actual == metric)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」metric 设为 {Metric}（读回校验通过）。", iface, metric);
            return new RouteMetricResult(true, iface, $"已把「{iface}」的优先级调到最高（metric={metric}）。");
        }

        if (actual is null && ifconfig.IsSuccess)
        {
            // 命令成功、但本机 ifconfig 不回报 metric —— 无法校验，如实说明而不是声称成功。
            string unverified = $"已对「{iface}」下发 metric={metric}，但本机 ifconfig 不回报该项，无法校验结果。";
            _logger.LogInformation("{Message}", unverified);
            return new RouteMetricResult(true, iface, unverified);
        }

        // 降级：route -n add -interface <iface>，让该接口承载默认路由。
        ProcessRunResult route = await RunProcessAsync(
            "route",
            ["-n", "add", "-interface", iface, "-net", "0.0.0.0", "0.0.0.0"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        int? actualAfterRoute = await ReadMetricAsync(iface, cancellationToken).ConfigureAwait(false);

        if (actualAfterRoute == metric || route.IsSuccess)
        {
            _logger.LogInformation("已通过 route 指定「{Iface}」承载路由（ifconfig metric 未生效）。", iface);
            return new RouteMetricResult(true, iface, $"已把「{iface}」设为承载路由的接口。");
        }

        string reason = !string.IsNullOrWhiteSpace(ifconfig.ErrorText) ? ifconfig.ErrorText.Trim()
            : !string.IsNullOrWhiteSpace(route.ErrorText) ? route.ErrorText.Trim()
            : $"读回值 {Describe(actual)} 与目标 {metric} 不一致";

        _logger.LogWarning("调整 ZeroTier 网卡「{Iface}」优先级失败：{Reason}", iface, reason);
        return new RouteMetricResult(
            false,
            iface,
            $"调整「{iface}」优先级未生效（{reason}）。可手动执行：sudo ifconfig {iface} metric {metric}");
    }

    /// <summary>读取 ifconfig 该接口输出的 metric（读不到返回 null）。</summary>
    private async Task<int?> ReadMetricAsync(string iface, CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "ifconfig",
            [iface],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        Match match = IfconfigMetricRegex().Match(result.Output);

        return match.Success
               && int.TryParse(match.Groups["metric"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
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

    private static string Describe(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "未知";

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
