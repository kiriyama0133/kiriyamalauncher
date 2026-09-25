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
/// Linux 实现：自动发现 ZeroTier 虚拟网卡并提高它的路由优先级。
///
/// 发现策略：
///   1. 用 <c>ip -o link show</c> 枚举网卡，按 <c>zt</c> 前缀（ZeroTier 官方命名约定）匹配；
///   2. 若传入网络 ID，优先命中名字带「网络 ID 低 16 位十六进制」的网卡（精确），否则取第一块 zt 网卡。
///
/// 调整策略（这里和 Windows/macOS 不同）：
///   - **Linux 没有「接口 metric」这个属性**：<c>metric</c> 是 BSD <c>ifconfig</c> 的概念，
///     <c>ip link set dev &lt;iface&gt; metric &lt;n&gt;</c> 并不是合法参数（iproute2 会直接报参数错误）。
///     在 Linux 上「接口优先级」实际由**经过该接口的路由的 metric** 决定，所以这里改写该接口承载的
///     IPv4 路由的 metric，语义等价且不动接口本身。
///   - 设置后**读回校验**：重新读取该接口路由的 metric，只有等于目标值才算成功；
///     权限不足（非 root）时给出可执行的 sudo 提示。
/// </summary>
public sealed partial class LinuxRouteMetricService : IRouteMetricService
{
    /// <summary>ZeroTier One 在 Linux 上的网卡名前缀。</summary>
    private const string ZEROTIER_IFACE_PREFIX = "zt";

    private readonly ILogger<LinuxRouteMetricService> _logger;

    public LinuxRouteMetricService(ILogger<LinuxRouteMetricService> logger)
    {
        _logger = logger;
    }

    /// <summary>IPv4 网段写法，如 10.99.33.0/24（源生成正则，NativeAOT 裁剪下安全）。</summary>
    [GeneratedRegex(@"^\d{1,3}(?:\.\d{1,3}){3}/\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4PrefixRegex();

    /// <summary>从路由行里取 src 地址（保留源地址选择语义）。</summary>
    [GeneratedRegex(@"\bsrc\s+(?<src>\d{1,3}(?:\.\d{1,3}){3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex RouteSrcRegex();

    /// <summary>从路由行里取 metric 值。</summary>
    [GeneratedRegex(@"\bmetric\s+(?<metric>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RouteMetricRegex();

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
                ? "未发现任何网络接口（ip link 枚举失败）。"
                : "未发现 ZeroTier 网卡（zt 前缀），可能尚未接入网络。";
            _logger.LogWarning("未命中 ZeroTier 网卡（候选 {Count} 块）：{Hint}", interfaces.Count, hint);
            return new RouteMetricResult(false, string.Empty, hint);
        }

        string iface = target.Value.Name;

        // 列出该接口承载的 IPv4 路由——这些路由的 metric 才是 Linux 上「接口优先级」的载体。
        IReadOnlyList<string> routeLines = await ReadRoutesAsync(iface, cancellationToken).ConfigureAwait(false);
        List<string> prefixes = ExtractPrefixes(routeLines);

        if (prefixes.Count == 0)
        {
            string hint = $"网卡「{iface}」当前没有可调整的 IPv4 路由（可能尚未真正接入网络）。";
            _logger.LogWarning("{Hint}", hint);
            return new RouteMetricResult(false, iface, hint);
        }

        string metricText = metric.ToString(CultureInfo.InvariantCulture);
        string lastError = string.Empty;

        foreach (string line in routeLines)
        {
            string? prefix = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (prefix is null || !Ipv4PrefixRegex().IsMatch(prefix))
            {
                continue;
            }

            // ip route replace <prefix> dev <iface> [src <addr>] metric <n>
            var arguments = new List<string> { "route", "replace", prefix, "dev", iface };

            Match src = RouteSrcRegex().Match(line);
            if (src.Success)
            {
                arguments.Add("src");
                arguments.Add(src.Groups["src"].Value);
            }

            arguments.Add("metric");
            arguments.Add(metricText);

            ProcessRunResult result = await RunProcessAsync(
                "ip",
                arguments,
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                lastError = result.ErrorText;
            }
        }

        // 读回校验：唯一可信的成功判据。
        int? actual = await ReadFirstRouteMetricAsync(iface, cancellationToken).ConfigureAwait(false);

        if (actual == metric)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」承载的 {Count} 条路由 metric 设为 {Metric}（读回校验通过）。", iface, prefixes.Count, metric);
            return new RouteMetricResult(true, iface, $"已把「{iface}」的优先级调到最高（metric={metric}）。");
        }

        if (IsPermissionError(lastError))
        {
            string sudoHint = $"调整 ZeroTier 网卡优先级需要 root 权限，请以 sudo 运行本应用，或手动执行：sudo ip route replace {prefixes[0]} dev {iface} metric {metric}";
            _logger.LogWarning("ZeroTier 网卡「{Iface}」metric 设置被拒绝：{Hint}", iface, sudoHint);
            return new RouteMetricResult(false, iface, sudoHint);
        }

        string reason = string.IsNullOrWhiteSpace(lastError)
            ? $"读回值 {Describe(actual)} 与目标 {metric} 不一致"
            : lastError.Trim();

        _logger.LogWarning("调整 ZeroTier 网卡「{Iface}」路由 metric 失败：{Reason}", iface, reason);
        return new RouteMetricResult(false, iface, $"调整「{iface}」优先级未生效（{reason}）。");
    }

    /// <summary>枚举网卡：ip -o link show。</summary>
    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "ip",
            ["-o", "link", "show"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return ParseIpLinkOutput(result.Output);
    }

    /// <summary>读取该接口的 IPv4 路由行（ip -o -4 route show dev &lt;iface&gt;）。</summary>
    private async Task<IReadOnlyList<string>> ReadRoutesAsync(string iface, CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "ip",
            ["-o", "-4", "route", "show", "dev", iface],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>取该接口 IPv4 路由里第一条带 metric 的路由的 metric 值（用于读回校验）。</summary>
    private async Task<int?> ReadFirstRouteMetricAsync(string iface, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines = await ReadRoutesAsync(iface, cancellationToken).ConfigureAwait(false);

        foreach (string line in lines)
        {
            Match match = RouteMetricRegex().Match(line);
            if (match.Success
                && int.TryParse(match.Groups["metric"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return null;
    }

    private static List<string> ExtractPrefixes(IReadOnlyList<string> routeLines)
    {
        var prefixes = new List<string>();

        foreach (string line in routeLines)
        {
            string? first = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null && Ipv4PrefixRegex().IsMatch(first) && !prefixes.Contains(first, StringComparer.Ordinal))
            {
                prefixes.Add(first);
            }
        }

        return prefixes;
    }

    /// <summary>解析 ip -o link show 输出：每行形如 "2: eth0: &lt;BROADCAST,...&gt; mtu 1500 ..."。网卡名在行首 "N: name:"。</summary>
    private static List<NetworkInterfaceInfo> ParseIpLinkOutput(string output)
    {
        var results = new List<NetworkInterfaceInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // 格式：<index>: <name>: <flags>
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            int nameStart = colon + 1;
            int nameEnd = line.IndexOf(':', nameStart);
            if (nameEnd < 0)
            {
                continue;
            }

            string name = line[nameStart..nameEnd].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // 行首的索引也一并留下（对齐 Windows 的按索引下发习惯，便于日后使用 @N 形式的命令）。
            int? ifIndex = int.TryParse(line[..colon].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) ? idx : null;

            results.Add(new NetworkInterfaceInfo(name, name, null, ifIndex));
        }

        return results;
    }

    /// <summary>从候选网卡里挑出 ZeroTier 网卡（zt 前缀）：优先精确命中网络 ID，其次取第一块。</summary>
    private static NetworkInterfaceInfo? PickZeroTierInterface(IReadOnlyList<NetworkInterfaceInfo> interfaces, ulong networkId)
    {
        if (interfaces.Count == 0)
        {
            return null;
        }

        string networkIdHex = networkId == 0
            ? string.Empty
            : networkId.ToString("x16", CultureInfo.InvariantCulture);

        // 第一优先：名字带网络 ID 的 zt 网卡。
        if (!string.IsNullOrWhiteSpace(networkIdHex))
        {
            NetworkInterfaceInfo? exact = interfaces.FirstOrDefault(
                i => IsZeroTierInterface(i) && i.Name.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                return exact;
            }
        }

        // 第二优先：任意 zt 网卡。
        return interfaces.FirstOrDefault(IsZeroTierInterface);
    }

    private static bool IsZeroTierInterface(NetworkInterfaceInfo i)
        => i.Name.StartsWith(ZEROTIER_IFACE_PREFIX, StringComparison.OrdinalIgnoreCase);

    private static bool IsPermissionError(string error)
        => error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
           || error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
           || error.Contains("permission", StringComparison.OrdinalIgnoreCase);

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
