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
/// Linux 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由度量（metric）。
///
/// 发现策略：
///   1. 用 <c>ip -o link show</c> 枚举网卡，按 <c>zt</c> 前缀（ZeroTier 官方命名约定）匹配；
///   2. 若传入网络 ID，优先命中名字带「网络 ID 低 16 位十六进制」的网卡（精确），否则取第一块 zt 网卡；
///   3. 命中后用 <c>ip link set dev &lt;iface&gt; metric &lt;metric&gt;</c> 调整（需要 root / sudo）。
/// </summary>
public sealed class LinuxRouteMetricService : IRouteMetricService
{
    /// <summary>ZeroTier One 在 Linux 上的网卡名前缀。</summary>
    private const string ZEROTIER_IFACE_PREFIX = "zt";

    private readonly ILogger<LinuxRouteMetricService> _logger;

    public LinuxRouteMetricService(ILogger<LinuxRouteMetricService> logger)
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
                ? "未发现任何网络接口（ip link 枚举失败）。"
                : "未发现 ZeroTier 网卡（zt 前缀），可能尚未接入网络。";
            _logger.LogWarning("未命中 ZeroTier 网卡（候选 {Count} 块）：{Hint}", interfaces.Count, hint);
            return new RouteMetricResult(false, string.Empty, hint);
        }

        ProcessRunResult result = await RunProcessAsync(
            "ip",
            ["link", "set", "dev", target.Value.Name, "metric", metric.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」metric 设为 {Metric}。", target.Value.Name, metric);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」的优先级调到最高（metric={metric}）。");
        }

        string message = result.ErrorText.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || result.ErrorText.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            ? $"调整「{target.Value.Name}」metric 需要 root 权限，请以 sudo 运行本应用或手动执行：sudo ip link set dev {target.Value.Name} metric {metric}"
            : $"调整「{target.Value.Name}」metric 失败：{result.ErrorText}";

        return new RouteMetricResult(false, target.Value.Name, message);
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

    /// <summary>解析 ip -o link show 输出：每行形如 "2: eth0: <BROADCAST,...> mtu 1500 ..."。网卡名在行首 "N: name:"。</summary>
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

            results.Add(new NetworkInterfaceInfo(name, name, null));
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
