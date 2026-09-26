using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
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
/// 若该系统版本的 ifconfig 压根不回报 metric，则如实标注「未能校验」，不谎报成功
/// ——注意此时也无法识别并列网卡（读不到 metric 就没法比较），并列消除会自动跳过。
///
/// 虚拟/实体的判定在 macOS 上没有系统级标志（<c>ifconfig -l</c> 只给名字），
/// 这里按名字关键字兜底：<c>zt*</c>/<c>feth*</c>/<c>utun*</c>/<c>tun*</c>/<c>tap*</c>/<c>ppp*</c>
/// 等算虚拟，<c>en*</c> 这类内置网卡算实体，从而保证只会下调「另一个虚拟网卡」。
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

        NetworkInterfaceInfo? target = RouteMetricPolicy.PickTargetInterface(interfaces, networkId, IsZeroTierInterface);

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

        bool unverified = false;

        if (actual == metric)
        {
            // 已生效，继续走并列消除。
        }
        else if (actual is null && ifconfig.IsSuccess)
        {
            // 命令成功、但本机 ifconfig 不回报 metric —— 无法校验，如实说明而不是声称成功。
            unverified = true;
        }
        else
        {
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

        // 消除并列：本机别的虚拟网卡若也处于最高优先级，把它们下调一档（详见 RouteMetricPolicy）。
        (List<string> demoted, List<string> physicalConflicts) =
            await DemoteConflictingInterfacesAsync(target.Value, metric, interfaces, cancellationToken).ConfigureAwait(false);

        string note = RouteMetricPolicy.BuildConflictNote(demoted, physicalConflicts);

        if (unverified)
        {
            string message = $"已对「{iface}」下发 metric={metric}，但本机 ifconfig 不回报该项，无法校验结果{note}。";
            _logger.LogInformation("{Message}", message);
            return new RouteMetricResult(true, iface, message, demoted);
        }

        _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」metric 设为 {Metric}（读回校验通过）。{Note}", iface, metric, note);
        return new RouteMetricResult(true, iface, $"已把「{iface}」的优先级调到最高（metric={metric}）{note}", demoted);
    }

    /// <summary>
    /// 消除并列：把本机其它**同样处于最高优先级**（metric ≤ 目标值）的虚拟网卡下调一档。
    /// 只做 ifconfig 下发 + 读回校验（不再走 route 降级——下调是次要动作，不必那么大动干戈）。
    /// 并列的实体网卡不修改，只回报名字供日志与提示。
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
        string demotedText = demotedMetric.ToString(CultureInfo.InvariantCulture);

        foreach (NetworkInterfaceInfo candidate in candidates)
        {
            ProcessRunResult ifconfig = await RunProcessAsync(
                "ifconfig",
                [candidate.Name, "metric", demotedText],
                TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            int? readBack = await ReadMetricAsync(candidate.Name, cancellationToken).ConfigureAwait(false);

            if (readBack == demotedMetric)
            {
                _logger.LogInformation(
                    "检测到并列最高优先级：已把虚拟网卡「{Name}」的 metric 由 {Old} 下调为 {New}（读回校验通过）。",
                    candidate.Name, Describe(candidate.Metric), demotedMetric);
                demoted.Add(candidate.Name);
            }
            else
            {
                _logger.LogWarning(
                    "并列网卡「{Name}」下调失败（读回 {Actual}，目标 {Wanted}；{Error}）—— 本网卡仍为最高，但并列未消除。",
                    candidate.Name, Describe(readBack), demotedMetric,
                    string.IsNullOrWhiteSpace(ifconfig.ErrorText) ? "无输出" : ifconfig.ErrorText.Trim());
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

    /// <summary>
    /// 枚举网卡：优先 <c>ifconfig -a</c>（一次拿到全部网卡及各自 metric），解析失败时退回
    /// <c>ifconfig -l</c>（只有名字，此时读不到 metric，并列比对会自动跳过）。
    /// </summary>
    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult all = await RunProcessAsync(
            "ifconfig",
            ["-a"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        List<NetworkInterfaceInfo> parsed = ParseIfconfigOutput(all.Output);

        if (parsed.Count > 0)
        {
            return parsed;
        }

        _logger.LogWarning("ifconfig -a 解析不到网卡，退回 ifconfig -l（读不到 metric，无法做并列比对）。");

        ProcessRunResult list = await RunProcessAsync(
            "ifconfig",
            ["-l"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return ParseIfconfigList(list.Output);
    }

    /// <summary>
    /// 解析 <c>ifconfig -a</c> 的分段输出：顶格行（不以空白开头）是新网卡的开始
    /// （形如 <c>en0: flags=8863&lt;UP,BROADCAST,...&gt; mtu 1500</c>），metric 若被回报则在段内。
    /// </summary>
    private static List<NetworkInterfaceInfo> ParseIfconfigOutput(string output)
    {
        var results = new List<NetworkInterfaceInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        string? currentName = null;
        var currentBlock = new StringBuilder();

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                AddIfconfigEntry(results, currentName, currentBlock.ToString());

                int colon = line.IndexOf(':');
                currentName = colon > 0 ? line[..colon].Trim() : null;
                currentBlock.Clear();
                currentBlock.AppendLine(line);
                continue;
            }

            if (currentName is not null)
            {
                currentBlock.AppendLine(line);
            }
        }

        AddIfconfigEntry(results, currentName, currentBlock.ToString());

        return results;
    }

    /// <summary>把一段 ifconfig 输出收成一条网卡记录（metric 读不到就是 null）。</summary>
    private static void AddIfconfigEntry(List<NetworkInterfaceInfo> results, string? name, string block)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Match match = IfconfigMetricRegex().Match(block);

        int? metric = match.Success
                      && int.TryParse(match.Groups["metric"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

        // macOS 没有「这块网卡是不是虚拟」的系统标志，用名字关键字兜底：
        // zt/feth/utun/tun… 判为虚拟（可以下调）、en* 等内置网卡判为实体（不动）。
        bool isVirtual = RouteMetricPolicy.LooksLikeVirtualInterface(name, name);

        results.Add(new NetworkInterfaceInfo(name, name, metric, null, isVirtual));
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
            string trimmed = name.Trim();

            results.Add(new NetworkInterfaceInfo(
                trimmed,
                trimmed,
                null,
                null,
                RouteMetricPolicy.LooksLikeVirtualInterface(trimmed, trimmed)));
        }

        return results;
    }

    /// <summary>目标网卡的判定谓词：zt 前缀（挑选逻辑在 RouteMetricPolicy）。</summary>
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
