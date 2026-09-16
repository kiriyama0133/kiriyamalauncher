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
/// Windows 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由度量（metric）。
///
/// 发现策略（不硬编码网卡名）：
///   1. 用 PowerShell 的 Get-NetAdapter 枚举全部网卡，按别名（Name）或描述（InterfaceDescription）
///      含 <c>ZeroTier</c> 关键字匹配；
///   2. 若传入网络 ID，则优先命中网卡名里带「网络 ID 低 16 位十六进制」的那块（精确），
///      否则退回关键字匹配的第一块；
///   3. 命中后用 Set-NetIPInterface -InterfaceMetric 调整（失败回退 netsh interface ipv4 set ... metric=）。
/// </summary>
public sealed class WindowsRouteMetricService : IRouteMetricService
{
    /// <summary>Windows 上 ZeroTier 网卡名/描述里的品牌关键字。</summary>
    private const string ZEROTIER_KEYWORD = "ZeroTier";

    private readonly ILogger<WindowsRouteMetricService> _logger;

    public WindowsRouteMetricService(ILogger<WindowsRouteMetricService> logger)
    {
        _logger = logger;
    }

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
                ? "未发现任何网络接口（PowerShell 枚举失败）。"
                : "未发现 ZeroTier 虚拟网卡，可能尚未接入网络。";
            _logger.LogWarning("未命中 ZeroTier 网卡（候选 {Count} 块）：{Hint}", interfaces.Count, hint);
            return new RouteMetricResult(false, string.Empty, hint);
        }

        // 2. 优先 PowerShell：Set-NetIPInterface 按 InterfaceAlias 精确命中。
        ProcessRunResult ps = await RunProcessAsync(
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                $"Set-NetIPInterface -InterfaceAlias '{target.Value.Name}' -InterfaceMetric {metric} -ErrorAction Stop"
            ],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (ps.IsSuccess)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Alias}」metric 设为 {Metric}。", target.Value.Name, metric);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」的优先级调到最高（metric={metric}）。");
        }

        // 3. 回退 netsh：不依赖 NetIPInterface cmdlet（精简系统 / 早期 PowerShell）。
        _logger.LogWarning("PowerShell 设置 metric 失败（{Error}），改用 netsh 重试。", ps.ErrorText);

        ProcessRunResult netsh = await RunProcessAsync(
            "netsh.exe",
            ["interface", "ipv4", "set", "interface", $"name=\"{target.Value.Name}\"", $"metric={metric}"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (netsh.IsSuccess)
        {
            _logger.LogInformation("已通过 netsh 将 ZeroTier 网卡「{Alias}」metric 设为 {Metric}。", target.Value.Name, metric);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」的优先级调到最高（metric={metric}）。");
        }

        return new RouteMetricResult(false, target.Value.Name, $"调整「{target.Value.Name}」metric 失败：{netsh.ErrorText}");
    }

    /// <summary>枚举系统网卡（PowerShell Get-NetAdapter），失败时退回 netsh interface show interface。</summary>
    private async Task<IReadOnlyList<NetworkInterfaceInfo>> EnumerateInterfacesAsync(CancellationToken cancellationToken)
    {
        // 优先 PowerShell：能同时拿到 Name（别名）和 InterfaceDescription。
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

        // 兜底：netsh interface show interface（只有名字，没有描述）。
        ProcessRunResult netsh = await RunProcessAsync(
            "netsh.exe",
            ["interface", "show", "interface"],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        return ParseNetshInterfaces(netsh.Output);
    }

    /// <summary>从 Get-NetAdapter 的 JSON 输出解析网卡列表。</summary>
    private static List<NetworkInterfaceInfo>? ParseNetAdapterJson(string json)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);

            var results = new List<NetworkInterfaceInfo>();

            // Get-NetAdapter 单个结果是对象，多个结果是数组；两者都要能解析。
            System.Text.Json.JsonElement root = doc.RootElement;
            foreach (System.Text.Json.JsonElement element in root.ValueKind == System.Text.Json.JsonValueKind.Array
                         ? root.EnumerateArray()
                         : ToSingle(root))
            {
                string name = element.TryGetProperty("Name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                    ? n.GetString() ?? string.Empty
                    : string.Empty;

                string description = element.TryGetProperty("InterfaceDescription", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                    ? d.GetString() ?? string.Empty
                    : name;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new NetworkInterfaceInfo(name.Trim(), description.Trim(), null));
            }

            return results;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>把单个 JsonElement 包装成单元素序列（替代 .NET 8 的 EnumerateArrayOrSingle）。</summary>
    private static System.Collections.Generic.IEnumerable<System.Text.Json.JsonElement> ToSingle(System.Text.Json.JsonElement element)
    {
        yield return element;
    }

    /// <summary>从 netsh interface show interface 的表格文本解析网卡名。</summary>
    private static List<NetworkInterfaceInfo> ParseNetshInterfaces(string output)
    {
        var results = new List<NetworkInterfaceInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        // 输出形如：
        //   管理状态    状态          类型       接口名称
        //   已启用       已连接        专用       Ethernet
        //   已启用       已连接        专用       ZeroTier One [bb40b36408000001]
        // 接口名称在行尾，直接按行取最后一列即可。
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("管理状态", StringComparison.Ordinal) || trimmed.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            // 接口名称是行里最后一个非空 token（可能含空格，取「类型」列之后的部分）。
            int typeIndex = trimmed.LastIndexOf("专用", StringComparison.Ordinal);
            if (typeIndex < 0)
            {
                typeIndex = trimmed.LastIndexOf("专用", StringComparison.OrdinalIgnoreCase);
            }

            string name = typeIndex >= 0
                ? trimmed[(typeIndex + "专用".Length)..].Trim()
                : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();

            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(new NetworkInterfaceInfo(name, name, null));
            }
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
