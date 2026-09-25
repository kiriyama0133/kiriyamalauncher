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
/// macOS 实现：自动发现 ZeroTier 虚拟网卡并调整它的路由优先级。
///
/// 说明：macOS 没有 Linux「ip link ... metric」那样直接的接口 metric 概念，但 ZeroTier One
/// 在 macOS 上会为每个网络分配一个 <c>zt&lt;id&gt;</c> 虚拟接口，其路由优先级由
/// <c>route -n add</c> 里指定 <c>-interface</c> 时生效。这里通过 <c>ifconfig -l</c> 枚举网卡
/// （ZeroTier One 同样用 <c>zt</c> 前缀），并用 <c>ifconfig &lt;iface&gt; metric &lt;n&gt;</c>
/// 尝试设置（较新 macOS 的 ifconfig 已支持 metric 参数）；若该参数不被支持，则降级为
/// <c>route -n add -interface &lt;iface&gt;</c> 指定走该接口，并返回可执行的手动提示。
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

        // 首选：较新 macOS 的 ifconfig 支持 metric 参数。
        ProcessRunResult result = await RunProcessAsync(
            "ifconfig",
            [target.Value.Name, "metric", metric.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation("已将 ZeroTier 网卡「{Iface}」metric 设为 {Metric}。", target.Value.Name, metric);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」的优先级调到最高（metric={metric}）。");
        }

        // 降级：route -n add -interface <iface>（对不支持 metric 参数的旧版 ifconfig 仍能指定路由走该接口）。
        string routeCommand = $"-n add -interface {target.Value.Name} -net 0.0.0.0 0.0.0.0";
        ProcessRunResult route = await RunProcessAsync(
            "route",
            routeCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        if (route.IsSuccess)
        {
            _logger.LogInformation("已通过 route 指定 ZeroTier 网卡「{Iface}」承载路由。", target.Value.Name);
            return new RouteMetricResult(true, target.Value.Name, $"已把「{target.Value.Name}」设为承载路由的接口。");
        }

        // 两者都失败：给出可执行的手动提示。
        string message = $"自动调整「{target.Value.Name}」优先级失败（ifconfig：{result.ErrorText}；route：{route.ErrorText}）。macOS 请手动执行：sudo route -n add -interface {target.Value.Name} -net 0.0.0.0 0.0.0.0";
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
