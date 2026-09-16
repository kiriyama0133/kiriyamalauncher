using kiriyamalauncher.Data.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// ZeroTier 传输后端工厂（策略模式）。
///
/// 把「用哪个后端」与「后端怎么干活」解耦：所有消费者继续依赖 <see cref="IZeroTierService"/>，
/// 工厂根据用户偏好的传输引擎（<see cref="ZeroTierSettings.SocketsBackend"/> /
/// <see cref="ZeroTierSettings.ClientBackend"/>）挑出对应的 <see cref="IZeroTierBackend"/> 转发过去。
///
/// 当前只实现了 Sockets 后端（<see cref="ZeroTierService"/>，libzt 内嵌节点）。
/// 未来接入「嵌入的 ZeroTier 客户端」时，新增一个 <see cref="IZeroTierBackend"/> 实现并注册进 DI，
/// 这里无需改动即可自动选中。
/// </summary>
public class ZeroTierBackendFactory : IZeroTierService, IDisposable
{
    private readonly IReadOnlyList<IZeroTierBackend> _backends;
    private readonly Func<string> _preferredKind;
    private readonly ILogger<ZeroTierBackendFactory> _logger;

    public ZeroTierBackendFactory(
        IEnumerable<IZeroTierBackend> backends,
        Func<string> preferredKind,
        ILogger<ZeroTierBackendFactory> logger)
    {
        _backends = backends.ToArray();
        _preferredKind = preferredKind;
        _logger = logger;
    }

    /// <summary>当前选中的后端（按用户偏好解析，匹配不到时回退到 Sockets）。</summary>
    private IZeroTierBackend Current => Resolve();

    /// <summary>当前生效的后端类型标识。</summary>
    public string CurrentKind => Current.Kind;

    /// <inheritdoc />
    public string Kind => Current.Kind;

    /// <inheritdoc />
    public string ConnectionMode
    {
        get => Current.ConnectionMode;
        set => Current.ConnectionMode = value;
    }

    public event EventHandler<string>? EventRaised
    {
        add => Current.EventRaised += value;
        remove => Current.EventRaised -= value;
    }

    public bool IsStarted => Current.IsStarted;

    public string LocalVirtualIp => Current.LocalVirtualIp;

    public string LocalNetworkPrefix => Current.LocalNetworkPrefix;

    public IReadOnlyList<ulong> JoinedNetworkIds => Current.JoinedNetworkIds;

    public Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default)
        => Current.StartAsync(storagePath, cancellationToken);

    public Task OrbitMoonAsync(ulong moonId, CancellationToken cancellationToken = default)
        => Current.OrbitMoonAsync(moonId, cancellationToken);

    public Task<ZeroTierStatus> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
        => Current.JoinNetworkAsync(networkId, cancellationToken);

    public Task<ZeroTierPingResult> PingAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
        => Current.PingAsync(host, port, timeout, cancellationToken);

    public Task<ZeroTierStatus> DisconnectAsync(ulong networkId, CancellationToken cancellationToken = default)
        => Current.DisconnectAsync(networkId, cancellationToken);

    public ZeroTierStatus GetStatus(ulong networkId)
        => Current.GetStatus(networkId);

    public Task RaiseVirtualInterfacePriorityAsync(CancellationToken cancellationToken = default)
        => Current.RaiseVirtualInterfacePriorityAsync(cancellationToken);

    public void Stop()
        => Current.Stop();

    /// <summary>根据偏好选择后端；偏好指定的后端不存在时回退到 Sockets。</summary>
    private IZeroTierBackend Resolve()
    {
        string preferred = _preferredKind();

        IZeroTierBackend? match = _backends.FirstOrDefault(
            backend => string.Equals(backend.Kind, preferred, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match;
        }

        IZeroTierBackend fallback = _backends.FirstOrDefault(
            backend => string.Equals(backend.Kind, ZeroTierSettings.SocketsBackend, StringComparison.OrdinalIgnoreCase))
            ?? _backends[0];

        if (!string.Equals(preferred, ZeroTierSettings.SocketsBackend, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "未找到传输引擎「{Preferred}」的实现，回退到「{Fallback}」。",
                preferred, fallback.Kind);
        }

        return fallback;
    }

    public void Dispose()
    {
        foreach (IZeroTierBackend backend in _backends)
        {
            (backend as IDisposable)?.Dispose();
        }
    }
}
