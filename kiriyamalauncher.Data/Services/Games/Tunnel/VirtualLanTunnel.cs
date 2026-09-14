using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IVirtualLanTunnel"/> 的默认实现。
///
/// 数据流：
///   游戏 Hook --(命名管道)--> 本类 --(libzt UDP)--> 对端虚拟 IP:同一个端口
///   游戏 Hook &lt;--(命名管道)-- 本类 &lt;--(libzt UDP)-- 对端虚拟 IP
///
/// 多人规则（和真实局域网一致）：
///   · 广播包（255.255.255.255 / x.x.x.255）→ 发给**所有**已知对端；
///   · 单播包 → 只发给目标 IP 那台对端（找不到就退化成发给所有对端）；
///   · 入站帧带上「真实来源 IP」，游戏收到的 from 才是对的。
///
/// 端口沿用游戏自己的端口（出站用游戏 socket 的本地端口，入站投给同一个端口），
/// 所以不需要事先知道对方用哪个端口。
/// </summary>
public class VirtualLanTunnel : IVirtualLanTunnel, IDisposable
{
    /// <summary>命名管道名（游戏侧 Hook 连这个）。</summary>
    public const string PIPE_NAME = "kiriyama-lan-tunnel";

    private readonly ILogger<VirtualLanTunnel> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<int, LibztUdpSocket> _sockets = [];
    private readonly Dictionary<int, CancellationTokenSource> _receiveSources = [];
    private readonly List<string> _peers = [];
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);

    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _source;
    private string _localVirtualIp = string.Empty;
    private volatile bool _clientConnected;
    private long _lastNoPeerWarningTicks;

    public VirtualLanTunnel(ILogger<VirtualLanTunnel> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsRunning => _pipe is not null;

    /// <inheritdoc />
    public bool IsClientConnected => _clientConnected;

    /// <inheritdoc />
    public string PeerVirtualIp
    {
        get
        {
            lock (_sync)
            {
                return _peers.Count > 0 ? _peers[0] : string.Empty;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PeerVirtualIps
    {
        get
        {
            lock (_sync)
            {
                return [.. _peers];
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<string>? StateChanged;

    /// <inheritdoc />
    public void Start(string localVirtualIp, IReadOnlyList<string> peerVirtualIps)
    {
        Stop();

        if (!IPAddress.TryParse(localVirtualIp?.Trim(), out IPAddress? localAddress))
        {
            throw new ArgumentException($"「{localVirtualIp}」不是合法的本机虚拟 IP。", nameof(localVirtualIp));
        }

        List<string> peers = NormalizePeers(peerVirtualIps);

        NamedPipeServerStream pipe = CreateServerPipe();
        CancellationTokenSource source = new();

        lock (_sync)
        {
            _pipe = pipe;
            _source = source;
            _clientConnected = false;
            _localVirtualIp = localAddress.ToString();
            _peers.Clear();
            _peers.AddRange(peers);
        }

        _ = Task.Run(() => AcceptLoopAsync(pipe, source.Token));

        _logger.LogInformation("游戏隧道已启动，等待游戏侧 Hook 连接（本机 {Local}，对端 {Peers}）。",
            _localVirtualIp, peers.Count == 0 ? "（还没发现设备）" : string.Join("、", peers));

        RaiseState(peers.Count == 0
            ? $"隧道已就绪：等待游戏侧 Hook 连接（本机 {_localVirtualIp}，还没发现对端设备）。"
            : $"隧道已就绪：等待游戏侧 Hook 连接（本机 {_localVirtualIp}，对端 {string.Join("、", peers)}）。");
    }

    /// <inheritdoc />
    public void UpdatePeers(IReadOnlyList<string> peerVirtualIps)
    {
        List<string> peers = NormalizePeers(peerVirtualIps);

        List<string> previous;

        lock (_sync)
        {
            if (_pipe is null)
            {
                return;
            }

            previous = [.. _peers];

            if (previous.Count == peers.Count && previous.All(peers.Contains))
            {
                return;
            }

            _peers.Clear();
            _peers.AddRange(peers);
        }

        _logger.LogInformation("隧道对端已更新：{Previous} → {Current}",
            previous.Count == 0 ? "（空）" : string.Join("、", previous),
            peers.Count == 0 ? "（空）" : string.Join("、", peers));

        RaiseState(peers.Count == 0
            ? "隧道在跑，但对端设备暂时都掉线了。"
            : $"隧道对端：{string.Join("、", peers)}。");
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? source;
        NamedPipeServerStream? pipe;
        List<LibztUdpSocket> sockets;
        List<CancellationTokenSource> receiveSources;

        lock (_sync)
        {
            source = _source;
            pipe = _pipe;
            sockets = [.. _sockets.Values];
            receiveSources = [.. _receiveSources.Values];

            _source = null;
            _pipe = null;
            _sockets.Clear();
            _receiveSources.Clear();
            _clientConnected = false;
            _peers.Clear();
        }

        if (source is null && pipe is null && sockets.Count == 0)
        {
            return;
        }

        try
        {
            source?.Cancel();
        }
        catch (Exception)
        {
            // 取消失败无所谓。
        }

        source?.Dispose();

        foreach (CancellationTokenSource item in receiveSources)
        {
            try
            {
                item.Cancel();
                item.Dispose();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }

        foreach (LibztUdpSocket socket in sockets)
        {
            socket.Dispose();
        }

        try
        {
            pipe?.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }

        _localVirtualIp = string.Empty;

        _logger.LogInformation("游戏隧道已停止。");
        RaiseState("隧道已停止。");
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    /// 显式给管道一个宽松的 ACL：游戏进程和启动器可能不在同一个完整性级别
    /// （比如其中一个以管理员身份启动），默认 ACL 会让注入进游戏的 Hook 拿到「拒绝访问」。
    /// </summary>
    private static NamedPipeServerStream CreateServerPipe()
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PIPE_NAME,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }

    private static List<string> NormalizePeers(IReadOnlyList<string>? peerVirtualIps)
    {
        List<string> peers = [];

        if (peerVirtualIps is null)
        {
            return peers;
        }

        foreach (string item in peerVirtualIps)
        {
            if (IPAddress.TryParse(item?.Trim(), out IPAddress? address))
            {
                string text = address.ToString();

                if (!peers.Contains(text))
                {
                    peers.Add(text);
                }
            }
        }

        return peers;
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "等待游戏侧 Hook 连接失败。");
            RaiseState($"等待 Hook 连接失败：{ex.Message}");
            return;
        }

        _clientConnected = true;

        _logger.LogInformation("游戏侧 Hook 已连接隧道。");
        RaiseState("游戏侧 Hook 已连接，隧道开始转发。");

        await SendControlAsync(cancellationToken);

        try
        {
            await ReadLoopAsync(pipe, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "管道读取结束。");
        }
        finally
        {
            _clientConnected = false;
            RaiseState("游戏侧 Hook 已断开。");
        }
    }

    /// <summary>把本机虚拟 IP 告诉 Hook：它用它算出虚拟网段，决定哪些单播包该交给隧道。</summary>
    private async Task SendControlAsync(CancellationToken cancellationToken)
    {
        string localIp;

        lock (_sync)
        {
            localIp = _localVirtualIp;
        }

        if (!IPAddress.TryParse(localIp, out IPAddress? address))
        {
            return;
        }

        byte[] frame = LanTunnelProtocol.BuildFrame(LanTunnelProtocol.FRAME_CONTROL, 0, 0, [], address);

        await WriteFrameAsync(frame, cancellationToken);

        _logger.LogInformation("已把本机虚拟 IP {Local} 告知游戏侧 Hook。", localIp);
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            (byte Type, byte Flags, int SourcePort, int DestinationPort, IPAddress Address, byte[] Payload)? frame =
                await LanTunnelProtocol.ReadFrameAsync(pipe, cancellationToken);

            if (frame is null)
            {
                return;
            }

            switch (frame.Value.Type)
            {
                case LanTunnelProtocol.FRAME_HELLO:
                    _logger.LogInformation("游戏侧 Hook 发来握手帧，隧道可以开始转发。");
                    break;

                case LanTunnelProtocol.FRAME_OUTBOUND:
                    SendToPeers(frame.Value);
                    break;

                case LanTunnelProtocol.FRAME_LISTEN:
                    EnsureListening(frame.Value.DestinationPort);
                    break;

                default:
                    _logger.LogDebug("忽略未知帧类型 {Type}。", frame.Value.Type);
                    break;
            }
        }
    }

    /// <summary>
    /// 游戏 bind 了某个本地端口 —— 隧道要在虚拟网上也把它打开，
    /// 否则对端发到「房主的监听端口（62900 这种）」的包会被直接丢掉。
    /// </summary>
    private void EnsureListening(int port)
    {
        if (port <= 0 || port > 65535)
        {
            return;
        }

        try
        {
            GetOrCreatePortSocket(port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "在虚拟网上监听端口 {Port} 失败。", port);
        }
    }

    /// <summary>
    /// 把游戏发出的报文发给对端：
    /// 广播 → 所有对端；单播 → 目标地址那台（找不到就退化成所有对端）。
    /// 本机虚拟网 socket 绑在游戏那个 socket 的本地端口上，所以对方回包能回到同一个端口。
    /// </summary>
    private void SendToPeers((byte Type, byte Flags, int SourcePort, int DestinationPort, IPAddress Address, byte[] Payload) frame)
    {
        List<string> targets;

        lock (_sync)
        {
            if (_peers.Count == 0)
            {
                WarnNoPeerThrottled(frame.SourcePort);
                return;
            }

            bool isBroadcast = (frame.Flags & LanTunnelProtocol.FLAG_BROADCAST) != 0;

            if (isBroadcast)
            {
                targets = [.. _peers];
            }
            else
            {
                string address = frame.Address.ToString();
                targets = _peers.Where(peer => string.Equals(peer, address, StringComparison.Ordinal)).ToList();

                if (targets.Count == 0)
                {
                    targets = [.. _peers];
                }
            }
        }

        try
        {
            LibztUdpSocket socket = GetOrCreatePortSocket(frame.SourcePort);

            foreach (string target in targets)
            {
                int sent = socket.Send(target, frame.DestinationPort, frame.Payload);

                _logger.LogDebug(
                    "隧道出站：{SourcePort} → {Peer}:{DestinationPort}，{Length} 字节（返回 {Sent}）。",
                    frame.SourcePort, target, frame.DestinationPort, frame.Payload.Length, sent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "隧道出站失败（{SourcePort} → {DestinationPort}）。", frame.SourcePort, frame.DestinationPort);
        }
    }

    /// <summary>
    /// 「有报文要发、但对端列表是空的」——游戏发出去的广播没人收，表现就是刷不出房间。
    /// 以前这里只记 Debug，排查时在日志里根本看不到，所以改成限流的警告。
    /// </summary>
    private void WarnNoPeerThrottled(int sourcePort)
    {
        long now = Environment.TickCount64;

        if (now - Interlocked.Read(ref _lastNoPeerWarningTicks) < 30_000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastNoPeerWarningTicks, now);

        _logger.LogWarning(
            "隧道出站被丢弃：还没有已知对端（本地端口 {Port}）。游戏发的广播没人收，房间里会发现不了任何房间。",
            sourcePort);
    }

    /// <summary>
    /// 取（或创建）某个本地端口的虚拟网 socket：出站用它发，入站也从它收，
    /// 这样端口映射天然一致 —— 游戏用哪个端口，我们就在虚拟网上绑哪个端口。
    /// 一个 socket 可以同时发给多个对端，所以多人时也只需要一个。
    /// </summary>
    private LibztUdpSocket GetOrCreatePortSocket(int port)
    {
        lock (_sync)
        {
            if (_sockets.TryGetValue(port, out LibztUdpSocket? existing))
            {
                return existing;
            }

            LibztUdpSocket socket = new();

            if (!socket.Open(port))
            {
                throw new InvalidOperationException($"无法在虚拟网络上绑定端口 {port}：{socket.LastError}");
            }

            CancellationTokenSource source = new();

            _sockets[port] = socket;
            _receiveSources[port] = source;

            _ = Task.Run(() => ReceiveLoopAsync(port, socket, source.Token));

            _logger.LogInformation("隧道已在虚拟网络上绑定端口 {Port}，开始等待对端数据。", port);

            return socket;
        }
    }

    private async Task ReceiveLoopAsync(int port, LibztUdpSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[LanTunnelProtocol.MAX_PAYLOAD_LENGTH];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (socket.BytesAvailable <= 0)
                {
                    await Task.Delay(20, cancellationToken);
                    continue;
                }

                int received = socket.Receive(buffer, out string senderAddress, out int senderPort);

                if (received <= 0)
                {
                    continue;
                }

                _logger.LogDebug(
                    "隧道入站：{Sender}:{SenderPort} → 本地端口 {LocalPort}，{Length} 字节。",
                    senderAddress, senderPort, port, received);

                if (!IPAddress.TryParse(senderAddress, out IPAddress? address))
                {
                    address = IPAddress.Any;
                }

                // 入站帧：ipv4 = 对端源地址，srcPort = 对端源端口，dstPort = 本地要投递的端口。
                byte[] frame = LanTunnelProtocol.BuildFrame(
                    LanTunnelProtocol.FRAME_INBOUND, senderPort, port, buffer.AsSpan(0, received), address);

                await WriteFrameAsync(frame, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "隧道入站失败（端口 {Port}）。", port);

                try
                {
                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pipe;

        lock (_sync)
        {
            pipe = _pipe;
        }

        if (pipe is null || !_clientConnected)
        {
            return;
        }

        await _pipeWriteLock.WaitAsync(cancellationToken);

        try
        {
            await pipe.WriteAsync(frame, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入隧道管道失败。");
        }
        finally
        {
            _pipeWriteLock.Release();
        }
    }

    private void RaiseState(string message)
        => StateChanged?.Invoke(this, message);
}
