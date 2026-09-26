using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
///      同时用 <c>ip -o -4 route show</c> 一次性读回**每块网卡当前生效的 metric**（取该网卡各条路由的最小值），
///      以及用 sysfs（<c>/sys/class/net/&lt;name&gt;/device</c> 是否存在）判断虚拟/实体网卡；
///   2. 若传入网络 ID，优先命中名字带「网络 ID 低 16 位十六进制」的网卡（精确），否则取第一块 zt 网卡。
///
/// 调整策略（这里和 Windows/macOS 不同）：
///   - **Linux 没有「接口 metric」这个属性**：<c>metric</c> 是 BSD <c>ifconfig</c> 的概念，
///     <c>ip link set dev &lt;iface&gt; metric &lt;n&gt;</c> 并不是合法参数（iproute2 会直接报参数错误）。
///     在 Linux 上「接口优先级」实际由**经过该接口的路由的 metric** 决定，所以这里改写该接口承载的
///     IPv4 路由的 metric，语义等价且不动接口本身。
///   - 设置后**读回校验**：重新读取该接口路由的 metric，只有等于目标值才算成功；
///     权限不足（非 root）时给出可执行的 sudo 提示。
///   - **并列消除**：设好后若本机还有别的虚拟网卡（典型是另一块我们曾加入过的 zt 网卡）的 metric
///     同为最小值，并列时流量可能不走我们的隧道，于是把这些网卡的路由 metric 下调一档
///     （详见 <see cref="RouteMetricPolicy"/>）。实体网卡不动。
///   - 范围限于 IPv4：与「设置自己」保持同一口径（IPv6 路由重写风险更高，暂不触碰）。
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

    /// <summary>从路由行里取出口网卡名（<c>dev &lt;name&gt;</c>）。</summary>
    [GeneratedRegex(@"\bdev\s+(?<dev>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex RouteDevRegex();

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NetworkInterfaceInfo> interfaces = await EnumerateInterfacesAsync(cancellationToken).ConfigureAwait(false);

        NetworkInterfaceInfo? target = RouteMetricPolicy.PickTargetInterface(interfaces, networkId, IsZeroTierInterface);

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

        (int appliedCount, string lastError) = await ApplyRouteMetricAsync(iface, routeLines, metric, cancellationToken).ConfigureAwait(false);

        // 读回校验：唯一可信的成功判据。
        int? actual = await ReadFirstRouteMetricAsync(iface, cancellationToken).ConfigureAwait(false);

        if (actual == metric)
        {
            // 消除并列：本机别的虚拟网卡若也处在最高优先级，把它们下调一档（详见 RouteMetricPolicy）。
            (List<string> demoted, List<string> physicalConflicts) =
                await DemoteConflictingInterfacesAsync(target.Value, metric, interfaces, cancellationToken).ConfigureAwait(false);

            string note = RouteMetricPolicy.BuildConflictNote(demoted, physicalConflicts);

            _logger.LogInformation(
                "已将 ZeroTier 网卡「{Iface}」承载的 {Count} 条路由 metric 设为 {Metric}（读回校验通过）。{Note}",
                iface, appliedCount, metric, note);

            return new RouteMetricResult(true, iface, $"已把「{iface}」的优先级调到最高（metric={metric}）{note}", demoted);
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

    /// <summary>
    /// 把指定接口承载的 IPv4 路由 metric 设为目标值（<c>ip route replace &lt;prefix&gt; dev &lt;iface&gt; [src ...] metric &lt;n&gt;</c>）。
    /// 返回最后一条失败信息（全部成功时为空串），供调用方决定提示文案；成功与否由调用方读回校验。
    /// </summary>
    private async Task<(int Applied, string LastError)> ApplyRouteMetricAsync(
        string iface,
        IReadOnlyList<string> routeLines,
        int metric,
        CancellationToken cancellationToken)
    {
        string metricText = metric.ToString(CultureInfo.InvariantCulture);
        string lastError = string.Empty;
        int applied = 0;

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

            if (result.IsSuccess)
            {
                applied++;
            }
            else
            {
                lastError = result.ErrorText;
            }
        }

        return (applied, lastError);
    }

    /// <summary>
    /// 消除并列：把本机其它**同样处于最高优先级**（metric ≤ 目标值）的虚拟网卡承载的路由下调一档。
    /// 并列的实体网卡不修改，只回报名字供日志与提示；下调失败只记告警，不计入成功名单。
    /// </summary>
    private async Task<(List<string> Demoted, List<string> PhysicalConflicts)> DemoteConflictingInterfacesAsync(
        NetworkInterfaceInfo target,
        int targetMetric,
        IReadOnlyList<NetworkInterfaceInfo> interfaces,
        CancellationToken cancellationToken)
    {
        var physicalConflicts = new List<NetworkInterfaceInfo>();
        List<NetworkInterfaceInfo> candidates =
            RouteMetricPolicy.SelectConflictCandidates(interfaces, target, targetMetric, physicalConflicts);

        var demoted = new List<string>();
        int demotedMetric = RouteMetricPolicy.DemotedMetric(targetMetric);

        foreach (NetworkInterfaceInfo candidate in candidates)
        {
            IReadOnlyList<string> lines = await ReadRoutesAsync(candidate.Name, cancellationToken).ConfigureAwait(false);

            if (ExtractPrefixes(lines).Count == 0)
            {
                _logger.LogInformation("并列网卡「{Name}」当前没有 IPv4 路由，无需下调。", candidate.Name);
                continue;
            }

            (int applied, string error) = await ApplyRouteMetricAsync(candidate.Name, lines, demotedMetric, cancellationToken).ConfigureAwait(false);
            int? readBack = await ReadFirstRouteMetricAsync(candidate.Name, cancellationToken).ConfigureAwait(false);

            if (readBack == demotedMetric)
            {
                _logger.LogInformation(
                    "检测到并列最高优先级：已把虚拟网卡「{Name}」的 {Count} 条路由 metric 由 {Old} 下调为 {New}（读回校验通过）。",
                    candidate.Name, applied, Describe(candidate.Metric), demotedMetric);
                demoted.Add(candidate.Name);
            }
            else
            {
                _logger.LogWarning(
                    "并列网卡「{Name}」下调失败（读回 {Actual}，目标 {Wanted}；{Error}）—— 本网卡仍为最高，但并列未消除。",
                    candidate.Name, Describe(readBack), demotedMetric,
                    string.IsNullOrWhiteSpace(error) ? "无输出" : error.Trim());
            }
        }

        foreach (NetworkInterfaceInfo item in physicalConflicts)
        {
            _logger.LogWarning(
                "实体网卡「{Name}」的 metric 同为 {Metric}，与 ZeroTier 网卡并列；按策略不修改实体网卡（需要隧道优先时请手动调整）。",
                item.Name, Describe(item.Metric));
        }

        return (demoted, physicalConflicts.Select(static p => p.Name).ToList());
    }

    /// <summary>
    /// 枚举网卡：<c>ip -o link show</c> 取名字与索引，<c>ip -o -4 route show</c> 取每块网卡当前生效的 metric
    /// （同一网卡可能有多条路由，取最小值代表它的优先级），sysfs 判断虚拟/实体。
    /// </summary>
    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "ip",
            ["-o", "link", "show"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        List<NetworkInterfaceInfo> links = ParseIpLinkOutput(result.Output);

        if (links.Count == 0)
        {
            return links;
        }

        Dictionary<string, int> metrics = await ReadInterfaceMetricsAsync(cancellationToken).ConfigureAwait(false);

        var interfaces = new List<NetworkInterfaceInfo>(links.Count);

        foreach (NetworkInterfaceInfo link in links)
        {
            int? metric = metrics.TryGetValue(link.Name, out int value) ? value : null;

            interfaces.Add(link with { Metric = metric, IsVirtual = DetectVirtualInterface(link.Name) });
        }

        return interfaces;
    }

    /// <summary>
    /// 一次性读回所有接口的 IPv4 路由 metric：<c>ip -o -4 route show</c> 每行里取 <c>dev</c> 与 <c>metric</c>，
    /// 同一接口取最小值（越小越优先）。
    /// </summary>
    private async Task<Dictionary<string, int>> ReadInterfaceMetricsAsync(CancellationToken cancellationToken)
    {
        var metrics = new Dictionary<string, int>(StringComparer.Ordinal);

        ProcessRunResult result = await RunProcessAsync(
            "ip",
            ["-o", "-4", "route", "show"],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return metrics;
        }

        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match dev = RouteDevRegex().Match(line);
            Match metric = RouteMetricRegex().Match(line);

            if (!dev.Success
                || !metric.Success
                || !int.TryParse(metric.Groups["metric"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                continue;
            }

            string name = dev.Groups["dev"].Value;

            if (!metrics.TryGetValue(name, out int existing) || value < existing)
            {
                metrics[name] = value;
            }
        }

        return metrics;
    }

    /// <summary>
    /// 用 sysfs 判断虚拟网卡：实体网卡在 <c>/sys/class/net/&lt;name&gt;/device</c> 有软链（指向 PCI/USB 设备），
    /// 虚拟网卡（zt / tun / tap / wg / docker / veth…）没有。读不到 sysfs 时返回 null，
    /// 交给 <see cref="RouteMetricPolicy.IsVirtualInterface"/> 按名字关键字兜底。
    /// </summary>
    private static bool? DetectVirtualInterface(string name)
    {
        try
        {
            string root = Path.Combine("/sys/class/net", name);

            if (!Directory.Exists(root))
            {
                return null;
            }

            string device = Path.Combine(root, "device");

            return Directory.Exists(device) || File.Exists(device) ? false : true;
        }
        catch (Exception)
        {
            // sysfs 在某些容器/受限环境不可读 —— 拿不到就交给关键字兜底，不影响主流程。
            return null;
        }
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

    /// <summary>目标网卡的判定谓词：zt 前缀（挑选逻辑在 RouteMetricPolicy）。</summary>
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
