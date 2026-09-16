using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 不支持平台的兜底实现：不调整系统网卡 metric，只返回手动提示。
/// </summary>
public sealed class UnsupportedRouteMetricService : IRouteMetricService
{
    private readonly ILogger<UnsupportedRouteMetricService>? _logger;

    public UnsupportedRouteMetricService(ILogger<UnsupportedRouteMetricService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<RouteMetricResult> RaiseZeroTierMetricAsync(ulong networkId, int metric, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("当前平台不支持自动调整 ZeroTier 网卡 metric，返回手动提示。");

        return Task.FromResult(new RouteMetricResult(
            false,
            string.Empty,
            "当前平台不支持自动调整虚拟网卡优先级，请手动把 ZeroTier 虚拟网卡的 metric 调到最小。"));
    }
}
