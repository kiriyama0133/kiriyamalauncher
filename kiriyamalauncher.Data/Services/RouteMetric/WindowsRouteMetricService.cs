using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// Windows 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由度量（metric）。
///
/// 发现策略（不硬编码网卡名）：
///   1. 用 PowerShell 的 Get-NetAdapter 枚举全部网卡（回退 <c>netsh interface ipv4 show interfaces</c>），
///      按别名或描述含 <c>ZeroTier</c> 关键字匹配；
///   2. 若传入网络 ID，优先命中名字里带「网络 ID 低 16 位十六进制」的那块（精确），否则取第一块。
///
/// 下发与校验（这里是本服务真正的健壮性所在）：
///   - **一律用接口索引（ifIndex）下发，不用别名。** 别名形如
///     <c>ZeroTier One [bb40b36408000001]</c>，其中方括号在 PowerShell 的 -InterfaceAlias 里是
///     「字符集」通配符，导致 <c>Set-NetIPInterface -InterfaceAlias '...'</c> 静默匹配 0 个对象、
///     不报错、退出码 0 —— 表现为「日志说成功，metric 其实没变」。索引是纯数字，不受通配符影响。
///   - **下发后必须读回校验。** <c>Set-NetIPInterface</c>、<c>netsh</c> 都存在「命令没生效但退出码为 0」
///     的情况（netsh 语法错误时也打印用法并返回 0），因此只看退出码会把失败当成功。这里在设置后
///     重新读取 InterfaceMetric，只有读回值等于目标值才算成功。
///   - netsh 兜底命令的正确语法是 <c>netsh interface ipv4 set interface &lt;索引|名称&gt; metric=&lt;n&gt;</c>，
///     注意参数名不是 <c>name=</c>。
/// </summary>
public sealed partial class WindowsRouteMetricService : IRouteMetricService
{
    /// <summary>Windows 上 ZeroTier 网卡名/描述里的品牌关键字。</summary>
    private const string ZEROTIER_KEYWORD = "ZeroTier";

    private readonly ILogger<WindowsRouteMetricService> _logger;

    public WindowsRouteMetricService(ILogger<WindowsRouteMetricService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 匹配 <c>netsh interface ipv4 show interfaces</c> 的一行：Idx / Met / MTU / State / Name。
    /// 用源生成正则（GeneratedRegex）而不是运行时 new Regex —— 项目以 NativeAOT + 全裁剪发布，
    /// 源生成正则在裁剪下完全安全。
    /// </summary>
    [GeneratedRegex(@"^\s*(?<idx>\d+)\s+(?<met>\d+)\s+(?<mtu>\d+)\s+(?<state>\S+)\s+(?<name>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NetshInterfaceRowRegex();

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default)
    {
        // 1. 枚举网卡并过滤出 ZeroTier 网卡。
        IReadOnlyList<NetworkInterfaceInfo> interfaces = await EnumerateInterfacesAsync(cancellationToken).ConfigureAwait(false);

        NetworkInterfaceInfo? target = PickZeroTierInterface(interfaces, networkId);

        if (target is null)
        {
            string hint = interfaces.Count == 0
                ? "未发现任何网络接口（PowerShell 与 netsh 枚举均失败）。"
                : "未发现 ZeroTier 虚拟网卡，可能尚未接入网络。";
            _logger.LogWarning("未命中 ZeroTier 网卡（候选 {Count} 块）：{Hint}", interfaces.Count, hint);
            return new RouteMetricResult(false, string.Empty, hint);
        }

        string ifaceName = target.Value.Name;
        int ifIndex = target.Value.IfIndex ?? 0;

        if (ifIndex <= 0)
        {
            // 拿不到索引就退化不了：按别名下发正是要避开的坑（方括号通配符）。
            // 与其冒险给一个「看起来成功」的结果，不如明确报失败。
            const string hint = "未能取得 ZeroTier 网卡的接口索引（ifIndex），无法可靠地调整优先级。";
            _logger.LogWarning("ZeroTier 网卡「{Alias}」缺少 ifIndex：{Hint}", ifaceName, hint);
            return new RouteMetricResult(false, ifaceName, hint);
        }

        // 2. 首选 PowerShell：按接口索引同时设置 IPv4/IPv6 并读回实际值（一次进程完成）。
        (bool psOk, int? psV4, int? psV6, string psDetail) = await SetAndReadByIndexAsync(ifIndex, metric, cancellationToken).ConfigureAwait(false);

        if (psOk)
        {
            _logger.LogInformation(
                "已将 ZeroTier 网卡「{Alias}」(ifIndex={Index}) metric 设为 {Metric}（读回校验通过，IPv6={V6}）。",
                ifaceName, ifIndex, metric, Describe(psV6));
            return new RouteMetricResult(true, ifaceName, $"已把「{ifaceName}」的优先级调到最高（metric={metric}）。");
        }

        // 3. 回退 netsh：语法是 set interface <索引> metric=<n>（不是 name=...）。
        //    注意 netsh 即使语法错误也返回退出码 0，所以这里不信任退出码，仍以读回为准。
        _logger.LogWarning("PowerShell 按索引设置 metric 未生效（{Detail}），改用 netsh 重试。", psDetail);

        ProcessRunResult netshV4 = await RunProcessAsync(
            "netsh.exe",
            ["interface", "ipv4", "set", "interface", ifIndex.ToString(CultureInfo.InvariantCulture), $"metric={metric}"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        ProcessRunResult netshV6 = await RunProcessAsync(
            "netsh.exe",
            ["interface", "ipv6", "set", "interface", ifIndex.ToString(CultureInfo.InvariantCulture), $"metric={metric}"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        // 4. 读回校验：唯一可信的成功判据。
        (int? v4, int? v6, string readError) = await ReadMetricsAsync(ifIndex, cancellationToken).ConfigureAwait(false);

        if (v4 == metric)
        {
            _logger.LogInformation(
                "已通过 netsh 将 ZeroTier 网卡「{Alias}」(ifIndex={Index}) metric 设为 {Metric}（读回校验通过，IPv6={V6}）。",
                ifaceName, ifIndex, metric, Describe(v6));
            return new RouteMetricResult(true, ifaceName, $"已把「{ifaceName}」的优先级调到最高（metric={metric}）。");
        }

        string netshDetail = FirstNonEmpty(netshV4.ErrorText, netshV6.ErrorText);
        string allDetail = FirstNonEmpty(readError, netshDetail, psDetail);
        bool isElevation = IsElevationError(psDetail) || IsElevationError(netshDetail) || IsElevationError(readError);

        _logger.LogWarning(
            "调整 ZeroTier 网卡「{Alias}」(ifIndex={Index}) metric 失败：{Detail}",
            ifaceName, ifIndex, string.IsNullOrWhiteSpace(allDetail) ? "（无输出）" : allDetail);

        // 权限是最常见的失败原因（Debug 构建清单是 asInvoker，不自动提权），单独给一句可执行的建议。
        string message = isElevation
            ? $"调整「{ifaceName}」优先级失败：需要管理员权限。请以管理员身份运行本程序（Debug 构建不会自动提权，请改用 Release 发布版，或让 IDE 以管理员身份启动）。"
            : $"调整「{ifaceName}」优先级未生效（{allDetail}）。";

        return new RouteMetricResult(false, ifaceName, message);
    }

    /// <summary>
    /// 用一次 PowerShell 调用把目标网卡的 IPv4/IPv6 metric 都设为目标值，并**读回实际值**。
    /// 返回的 <c>Ok</c> 以读回值为准，而不是以命令退出码为准；失败时带回脚本里捕获到的真实原因
    /// （如 "Access is denied."，这通常意味着进程没有提权）。
    /// </summary>
    private async Task<(bool Ok, int? V4, int? V6, string Detail)> SetAndReadByIndexAsync(int ifIndex, int metric, CancellationToken cancellationToken)
    {
        // 注意：脚本里的 { } 一律用字符串拼接，避免 C# 插值的花括号转义；只让 ifIndex/metric 走插值。
        string script =
            "$ErrorActionPreference='Stop';" +
            "$r=[ordered]@{};$e='';" +
            "foreach($f in 'IPv4','IPv6'){" +
                "try{" +
                    $"Set-NetIPInterface -InterfaceIndex {ifIndex} -AddressFamily $f -InterfaceMetric {metric} -ErrorAction Stop;" +
                    $"$r[$f]=(Get-NetIPInterface -InterfaceIndex {ifIndex} -AddressFamily $f -ErrorAction Stop).InterfaceMetric" +
                "}catch{" +
                    "$r[$f]=-1;if(-not $e){$e=$_.Exception.Message}" +
                "}" +
            "};" +
            "$r['Error']=$e;" +
            "$r|ConvertTo-Json -Compress";

        ProcessRunResult ps = await RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

        (int? v4, int? v6, string scriptError) = ParseFamilyMetrics(ps.Output);

        bool ok = v4 == metric;
        string detail = ok
            ? string.Empty
            : FirstNonEmpty(scriptError, ps.ErrorText, $"IPv4 读回 {Describe(v4)} / IPv6 读回 {Describe(v6)}");

        return (ok, v4, v6, detail);
    }

    /// <summary>只读校验：读取目标网卡 IPv4/IPv6 当前的 metric。</summary>
    private async Task<(int? V4, int? V6, string Error)> ReadMetricsAsync(int ifIndex, CancellationToken cancellationToken)
    {
        string script =
            "$ErrorActionPreference='Stop';" +
            "$r=[ordered]@{};$e='';" +
            "foreach($f in 'IPv4','IPv6'){" +
                "try{" +
                    "$r[$f]=(Get-NetIPInterface -InterfaceIndex " + ifIndex.ToString(CultureInfo.InvariantCulture) + " -AddressFamily $f -ErrorAction Stop).InterfaceMetric" +
                "}catch{" +
                    "$r[$f]=-1;if(-not $e){$e=$_.Exception.Message}" +
                "}" +
            "};" +
            "$r['Error']=$e;" +
            "$r|ConvertTo-Json -Compress";

        ProcessRunResult ps = await RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        (int? v4, int? v6, string scriptError) = ParseFamilyMetrics(ps.Output);
        return (v4, v6, FirstNonEmpty(scriptError, ps.IsSuccess ? string.Empty : ps.ErrorText));
    }

    /// <summary>解析 <c>{"IPv4":1,"IPv6":1,"Error":""}</c>；-1 或缺失都视为「拿不到」。</summary>
    private static (int? V4, int? V6, string Error) ParseFamilyMetrics(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, string.Empty);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            return (ReadFamilyMetric(root, "IPv4"), ReadFamilyMetric(root, "IPv6"), ReadString(root, "Error"));
        }
        catch (JsonException)
        {
            return (null, null, string.Empty);
        }
    }

    private static int? ReadFamilyMetric(JsonElement root, string family)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(family, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)
            || value < 0)
        {
            return null;
        }

        return value;
    }

    private static string ReadString(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(property, out JsonElement element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>枚举系统网卡（PowerShell Get-NetAdapter），失败时退回 netsh interface ipv4 show interfaces。</summary>
    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        // 优先 PowerShell：能同时拿到 Name（别名）、InterfaceDescription、ifIndex。
        ProcessRunResult ps = await RunProcessAsync(
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                "Get-NetAdapter | Select-Object Name,InterfaceDescription,ifIndex | ConvertTo-Json -Compress"
            ],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (ps.IsSuccess && !string.IsNullOrWhiteSpace(ps.Output))
        {
            List<NetworkInterfaceInfo>? parsed = ParseNetAdapterJson(ps.Output);
            if (parsed is { Count: > 0 })
            {
                return parsed;
            }
        }

        _logger.LogWarning("PowerShell 枚举网卡失败，改用 netsh 兜底。");

        // 兜底：netsh interface ipv4 show interfaces（列：Idx / Met / MTU / State / Name）。
        // 不用 netsh interface show interface —— 那玩意儿的列头随系统语言变（中文「专用」/英文 Dedicated），
        // 解析很脆；这里这张表的列是固定数字列，与语言无关。
        ProcessRunResult netsh = await RunProcessAsync(
            "netsh.exe",
            ["interface", "ipv4", "show", "interfaces"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return ParseNetshInterfaces(netsh.Output);
    }

    /// <summary>从 Get-NetAdapter 的 JSON 输出解析网卡列表（含 ifIndex）。</summary>
    private static List<NetworkInterfaceInfo>? ParseNetAdapterJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            var results = new List<NetworkInterfaceInfo>();

            // Get-NetAdapter 单个结果是对象，多个结果是数组；两者都要能解析。
            JsonElement root = doc.RootElement;
            foreach (JsonElement element in root.ValueKind == JsonValueKind.Array
                         ? root.EnumerateArray()
                         : ToSingle(root))
            {
                string name = element.TryGetProperty("Name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? string.Empty
                    : string.Empty;

                string description = element.TryGetProperty("InterfaceDescription", out JsonElement d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? string.Empty
                    : name;

                int? ifIndex = element.TryGetProperty("ifIndex", out JsonElement ix)
                               && ix.ValueKind == JsonValueKind.Number
                               && ix.TryGetInt32(out int index)
                    ? index
                    : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new NetworkInterfaceInfo(name.Trim(), description.Trim(), null, ifIndex));
            }

            return results;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把单个 JsonElement 包装成单元素序列（替代 .NET 8 的 EnumerateArrayOrSingle）。</summary>
    private static IEnumerable<JsonElement> ToSingle(JsonElement element)
    {
        yield return element;
    }

    /// <summary>
    /// 从 <c>netsh interface ipv4 show interfaces</c> 的表格文本解析网卡（含索引与当前 metric）。
    /// 输出形如：
    /// <code>
    /// Idx     Met         MTU          State                Name
    /// ---  ----------  ----------  ------------  ---------------------------
    ///  69          35        2800  connected     ZeroTier One [bb40b36408000001]
    /// </code>
    /// </summary>
    private static List<NetworkInterfaceInfo> ParseNetshInterfaces(string output)
    {
        var results = new List<NetworkInterfaceInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = NetshInterfaceRowRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string name = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            int? ifIndex = int.TryParse(match.Groups["idx"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) ? idx : null;
            int? metric = int.TryParse(match.Groups["met"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int met) ? met : null;

            results.Add(new NetworkInterfaceInfo(name, name, metric, ifIndex));
        }

        return results;
    }

    /// <summary>从候选网卡里挑出 ZeroTier 网卡：优先精确命中网络 ID，其次关键字。</summary>
    private static NetworkInterfaceInfo? PickZeroTierInterface(IReadOnlyList<NetworkInterfaceInfo> interfaces, ulong networkId)
    {
        if (interfaces.Count == 0)
        {
            return null;
        }

        // 网络 ID 低 16 位十六进制（ZeroTier 网卡名通常带这段）。
        string networkIdHex = networkId == 0
            ? string.Empty
            : networkId.ToString("x16", CultureInfo.InvariantCulture);

        // 第一优先：名字里带网络 ID 且含关键字。
        if (!string.IsNullOrWhiteSpace(networkIdHex))
        {
            NetworkInterfaceInfo? exact = interfaces.FirstOrDefault(
                i => ContainsKeyword(i) && (i.Name.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase)
                                            || i.DisplayName.Contains(networkIdHex, StringComparison.OrdinalIgnoreCase)));

            if (exact is not null)
            {
                return exact;
            }
        }

        // 第二优先：名字或描述含 ZeroTier 关键字。
        return interfaces.FirstOrDefault(ContainsKeyword);
    }

    private static bool ContainsKeyword(NetworkInterfaceInfo i)
        => i.Name.Contains(ZEROTIER_KEYWORD, StringComparison.OrdinalIgnoreCase)
           || i.DisplayName.Contains(ZEROTIER_KEYWORD, StringComparison.OrdinalIgnoreCase);

    private static string Describe(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "未知";

    /// <summary>判断错误文本是否属于「权限不足」（PowerShell 报 Access is denied，netsh 报 requires elevation）。</summary>
    private static bool IsElevationError(string text)
        => text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
           || text.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Run as administrator", StringComparison.OrdinalIgnoreCase)
           || text.Contains("拒绝访问", StringComparison.Ordinal)
           || text.Contains("需要提升", StringComparison.Ordinal);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

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
