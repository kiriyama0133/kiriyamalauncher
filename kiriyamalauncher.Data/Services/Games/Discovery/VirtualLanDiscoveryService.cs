using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IVirtualLanDiscoveryService"/> 的默认实现。
///
/// 每轮扫描：
/// 1. 往虚拟局域网的广播地址发一个探测包（UDP，libzt socket）；
/// 2. 也在线的设备（运行着本程序的）收到后单播回一个应答；
/// 3. 我们用 recvfrom 拿到应答方的 IP 和往返时间，作为在线设备。
///
/// UDP 广播不可用时（socket 创建/绑定失败）自动回退成 TCP 扫描，保证功能不至于完全不可用。
/// </summary>
public class VirtualLanDiscoveryService : IVirtualLanDiscoveryService, IDisposable
{
    /// <summary>TCP 回退扫描的并发数。</summary>
    private const int MAX_PARALLEL_PROBES = 32;

    private const int FIRST_HOST = 1;
    private const int LAST_HOST = 254;

    /// <summary>发出探测包后等待应答的时间。</summary>
    private static readonly TimeSpan PROBE_WINDOW = TimeSpan.FromMilliseconds(1200);

    /// <summary>TCP 回退扫描时单个地址的超时。</summary>
    private static readonly TimeSpan PROBE_TIMEOUT = TimeSpan.FromMilliseconds(400);

    /// <summary>多久探测不到就认为离线。</summary>
    private static readonly TimeSpan LOST_AFTER = TimeSpan.FromSeconds(12);

    private const string PACKET_PREFIX = "KIRIYAMA1";
    private const string PACKET_PROBE = "probe";
    private const string PACKET_REPLY = "reply";

    private readonly IZeroTierService _zeroTier;
    private readonly ILogger<VirtualLanDiscoveryService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, VirtualLanPeer> _peers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly LibztUdpSocket _udp = new();

    /// <summary>本轮应答（设备 IP → 往返毫秒）。</summary>
    private readonly ConcurrentDictionary<string, long> _roundReplies = new(StringComparer.Ordinal);

    private CancellationTokenSource? _loopSource;
    private Task? _loop;
    private CancellationTokenSource? _receiveSource;
    private volatile string _activeNonce = string.Empty;
    private long _probeStartedTimestamp;
    private string _transportDescription = "尚未开始发现";
    private bool _loggedMissingVirtualIp;

    public VirtualLanDiscoveryService(IZeroTierService zeroTier, ILogger<VirtualLanDiscoveryService> logger)
    {
        _zeroTier = zeroTier;
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<VirtualLanPeer>? PeerDiscovered;

    /// <inheritdoc />
    public event EventHandler<VirtualLanPeer>? PeerLost;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<VirtualLanPeer>>? ScanCompleted;

    /// <inheritdoc />
    public event EventHandler? ScanningChanged;

    /// <inheritdoc />
    public IReadOnlyList<VirtualLanPeer> Peers
    {
        get
        {
            lock (_sync)
            {
                return _peers.Values.OrderBy(peer => peer.VirtualIp, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsScanning => _loop is not null;

    /// <inheritdoc />
    public string LocalVirtualIp => _zeroTier.LocalVirtualIp;

    /// <inheritdoc />
    public string LocalNetworkPrefix => _zeroTier.LocalNetworkPrefix;

    /// <inheritdoc />
    public string TransportDescription => _transportDescription;

    /// <inheritdoc />
    public async Task<IReadOnlyList<VirtualLanPeer>> ScanOnceAsync(
        string? ipPrefix = null,
        int probePort = IZeroTierService.PingPort,
        CancellationToken cancellationToken = default)
    {
        string prefix = NormalizePrefix(ipPrefix);
        string localIp = LocalVirtualIp;

        if (string.IsNullOrWhiteSpace(localIp))
        {
            if (!_loggedMissingVirtualIp)
            {
                _loggedMissingVirtualIp = true;
                _logger.LogInformation("还没有虚拟 IP，先连接到虚拟局域网再扫描（后续不再重复提示）。");
            }
            else
            {
                _logger.LogDebug("还没有虚拟 IP，跳过本轮扫描。");
            }

            return [];
        }

        _loggedMissingVirtualIp = false;

        // 同一时间只允许一轮扫描。
        if (!await _scanLock.WaitAsync(0, cancellationToken))
        {
            return Peers;
        }

        try
        {
            List<VirtualLanPeer> online = EnsureUdpTransport()
                ? await ScanByBroadcastAsync(prefix, localIp, cancellationToken)
                : await ScanByTcpAsync(prefix, probePort, cancellationToken);

            MergePeers(online);
            ScanCompleted?.Invoke(this, online);

            return online;
        }
        catch (OperationCanceledException)
        {
            return Peers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描虚拟局域网设备失败。");
            return Peers;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <inheritdoc />
    public void StartScanning(TimeSpan interval, string? ipPrefix = null, int probePort = IZeroTierService.PingPort)
    {
        StopScanning();

        if (interval < TimeSpan.FromSeconds(1))
        {
            interval = TimeSpan.FromSeconds(1);
        }

        CancellationTokenSource source = new();
        _loopSource = source;
        _loop = Task.Run(() => LoopAsync(interval, ipPrefix, probePort, source.Token), source.Token);

        _logger.LogInformation("开始轮询虚拟局域网设备（每 {Seconds} 秒一次）。", interval.TotalSeconds);
        ScanningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void StopScanning()
    {
        CancellationTokenSource? source = _loopSource;

        _loopSource = null;
        _loop = null;

        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (Exception)
        {
            // 取消失败无所谓。
        }

        source.Dispose();
        _logger.LogInformation("已停止轮询虚拟局域网设备。");
        ScanningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_sync)
        {
            _peers.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopScanning();

        _receiveSource?.Cancel();
        _receiveSource?.Dispose();
        _receiveSource = null;

        _udp.Dispose();
    }

    private async Task LoopAsync(TimeSpan interval, string? ipPrefix, int probePort, CancellationToken cancellationToken)
    {
        try
        {
            await ScanOnceAsync(ipPrefix, probePort, cancellationToken);

            using PeriodicTimer timer = new(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ScanOnceAsync(ipPrefix, probePort, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "轮询虚拟局域网设备时出错。");
        }
    }

    /// <summary>确保 UDP 发现 socket 可用（第一次扫描时创建并开始收包）。</summary>
    private bool EnsureUdpTransport()
    {
        if (_udp.IsOpen)
        {
            return true;
        }

        if (!_udp.Open(IVirtualLanDiscoveryService.DiscoveryPort))
        {
            _transportDescription = "TCP 扫描（UDP 广播不可用，已回退）";
            _logger.LogWarning(
                "UDP 发现 socket 创建失败，改用 TCP 扫描：{Error}",
                string.IsNullOrWhiteSpace(_udp.LastError) ? "未知原因" : _udp.LastError);
            return false;
        }

        _transportDescription = $"UDP 广播（端口 {IVirtualLanDiscoveryService.DiscoveryPort}）";

        _receiveSource = new CancellationTokenSource();
        CancellationToken token = _receiveSource.Token;

        Thread thread = new(() => ReceiveLoop(token))
        {
            IsBackground = true,
            Name = "kiriyama-lan-discovery"
        };

        thread.Start();

        _logger.LogInformation("虚拟局域网发现已启用 {Transport}。", _transportDescription);

        return true;
    }

    /// <summary>收包循环：应答别人的探测，记录所有的应答方。</summary>
    private void ReceiveLoop(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[2048];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_udp.BytesAvailable <= 0)
                {
                    Thread.Sleep(20);
                    continue;
                }

                int received = _udp.Receive(buffer, out string senderAddress, out int senderPort);

                if (received <= 0)
                {
                    continue;
                }

                HandleDatagram(buffer, received, senderAddress, senderPort);
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "接收虚拟局域网发现报文失败。");
                }

                Thread.Sleep(50);
            }
        }
    }

    private void HandleDatagram(byte[] buffer, int length, string senderAddress, int senderPort)
    {
        string text = Encoding.UTF8.GetString(buffer, 0, length);
        string[] parts = text.Split('|');

        if (parts.Length < 3 || !string.Equals(parts[0], PACKET_PREFIX, StringComparison.Ordinal))
        {
            return;
        }

        string kind = parts[1];
        string nonce = parts[2];

        // 不管收到的是探测还是应答，发送方本身就是一台在线设备。
        long roundTrip = 0;

        if (string.Equals(kind, PACKET_REPLY, StringComparison.Ordinal) && string.Equals(nonce, _activeNonce, StringComparison.Ordinal))
        {
            roundTrip = (long)Stopwatch.GetElapsedTime(_probeStartedTimestamp).TotalMilliseconds;
        }

        _roundReplies[senderAddress] = roundTrip;

        if (!string.Equals(kind, PACKET_PROBE, StringComparison.Ordinal))
        {
            return;
        }

        // 对方在找设备：单播回一个应答，让它也能发现我们。
        string reply = $"{PACKET_PREFIX}|{PACKET_REPLY}|{nonce}|{LocalVirtualIp}";
        _udp.Send(senderAddress, senderPort > 0 ? senderPort : IVirtualLanDiscoveryService.DiscoveryPort, Encoding.UTF8.GetBytes(reply));
    }

    /// <summary>广播探测一轮。</summary>
    private async Task<List<VirtualLanPeer>> ScanByBroadcastAsync(string prefix, string localIp, CancellationToken cancellationToken)
    {
        _roundReplies.Clear();

        string nonce = Guid.NewGuid().ToString("N")[..8];
        _activeNonce = nonce;
        _probeStartedTimestamp = Stopwatch.GetTimestamp();

        byte[] payload = Encoding.UTF8.GetBytes($"{PACKET_PREFIX}|{PACKET_PROBE}|{nonce}|{localIp}");

        // 同时往「受限广播」和「网段广播」各发一份，兼容不同的广播处理方式。
        int directResult = _udp.Send("255.255.255.255", IVirtualLanDiscoveryService.DiscoveryPort, payload);
        int subnetResult = -1;

        if (prefix.Length > 0)
        {
            subnetResult = _udp.Send($"{prefix}255", IVirtualLanDiscoveryService.DiscoveryPort, payload);
        }

        if (directResult < 0 && subnetResult < 0)
        {
            // 广播发不出去（例如平台不支持 SO_BROADCAST），这一轮改用 TCP 扫描。
            _logger.LogWarning("UDP 广播发送失败，本轮改用 TCP 扫描。");
            return await ScanByTcpAsync(prefix, IZeroTierService.PingPort, cancellationToken);
        }

        await Task.Delay(PROBE_WINDOW, cancellationToken);

        DateTime now = DateTime.Now;
        List<VirtualLanPeer> online = [];

        foreach ((string address, long roundTrip) in _roundReplies)
        {
            if (string.Equals(address, localIp, StringComparison.Ordinal))
            {
                continue;
            }

            online.Add(new VirtualLanPeer(address, roundTrip, now));
        }

        return online;
    }

    /// <summary>UDP 不可用时的回退：把网段逐个 TCP 探测一遍（需要对方在探测端口上有监听）。</summary>
    private async Task<List<VirtualLanPeer>> ScanByTcpAsync(string prefix, int probePort, CancellationToken cancellationToken)
    {
        if (prefix.Length == 0)
        {
            return [];
        }

        using SemaphoreSlim gate = new(MAX_PARALLEL_PROBES);
        List<Task<VirtualLanPeer?>> probes = [];

        for (int host = FIRST_HOST; host <= LAST_HOST; host++)
        {
            probes.Add(ProbeAsync($"{prefix}{host}", probePort, gate, cancellationToken));
        }

        VirtualLanPeer?[] results = await Task.WhenAll(probes);

        return [.. results.Where(peer => peer is not null).Select(peer => peer!)];
    }

    private async Task<VirtualLanPeer?> ProbeAsync(string address, int probePort, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (string.Equals(address, LocalVirtualIp, StringComparison.Ordinal))
            {
                return null;
            }

            ZeroTierPingResult result = await _zeroTier.PingAsync(address, probePort, PROBE_TIMEOUT, cancellationToken);

            return result.IsSuccess ? new VirtualLanPeer(address, result.RoundTripMilliseconds, DateTime.Now) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>把本轮结果并进设备表，并淘汰长时间没响应的设备。</summary>
    private void MergePeers(IReadOnlyList<VirtualLanPeer> online)
    {
        DateTime now = DateTime.Now;

        lock (_sync)
        {
            foreach (VirtualLanPeer peer in online)
            {
                bool isNew = !_peers.ContainsKey(peer.VirtualIp);
                VirtualLanPeer current = peer with { LastSeen = now };

                _peers[peer.VirtualIp] = current;

                if (isNew)
                {
                    _logger.LogInformation("发现虚拟局域网设备：{Peer}", current.DisplayText);
                    PeerDiscovered?.Invoke(this, current);
                }
            }

            foreach (string address in _peers.Keys.ToArray())
            {
                if (online.Any(peer => string.Equals(peer.VirtualIp, address, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (now - _peers[address].LastSeen < LOST_AFTER)
                {
                    continue;
                }

                VirtualLanPeer lost = _peers[address];
                _peers.Remove(address);

                _logger.LogInformation("设备已离线：{Address}", address);
                PeerLost?.Invoke(this, lost);
            }
        }
    }

    private string NormalizePrefix(string? ipPrefix)
    {
        string prefix = string.IsNullOrWhiteSpace(ipPrefix) ? LocalNetworkPrefix : ipPrefix.Trim();

        if (prefix.Length == 0)
        {
            return string.Empty;
        }

        return prefix.EndsWith('.') ? prefix : prefix + ".";
    }
}
