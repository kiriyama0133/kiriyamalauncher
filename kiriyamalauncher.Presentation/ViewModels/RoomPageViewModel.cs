using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 房间内页面：显示成员（昵称 + 虚拟 IP + 延迟），轮询探测延迟，
/// 订阅服务端事件流（房间解散 / 房主变更），并支持房主转让。
/// 由 <see cref="RoomsViewModel"/> 在加入房间后创建并挂到房间大厅的切换内容里。
/// </summary>
public sealed partial class RoomPageViewModel : BaseViewModel
{
    /// <summary>成员延迟轮询间隔。</summary>
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(5);

    /// <summary>单次 Ping 的超时。</summary>
    private static readonly TimeSpan PING_TIMEOUT = TimeSpan.FromSeconds(3);

    /// <summary>事件流断开后的重连间隔。</summary>
    private static readonly TimeSpan EVENT_RECONNECT_DELAY = TimeSpan.FromSeconds(3);

    private readonly IRelayServerClient _relay;
    private readonly IZeroTierService _zeroTier;
    private readonly IAppNotifier _notifier;
    private readonly ILogger<RoomPageViewModel> _logger;

    /// <summary>主动退出房间（会上报服务端清除本机 Tag）。</summary>
    private readonly Func<Task> _leaveRoom;

    /// <summary>被动退出（房间被服务端解散）：回大厅但不再上报 leave。</summary>
    private readonly Func<string?, Task> _roomClosedByServer;

    /// <summary>解析中继服务器 baseUrl 的委托（由 RoomsViewModel 提供）。</summary>
    private readonly Func<string> _resolveBaseUrl;

    /// <summary>成员延迟探测的后台循环（退出房间时取消）。</summary>
    private CancellationTokenSource? _pollCts;

    /// <summary>房间事件流（SSE）的后台循环（退出房间时取消）。</summary>
    private CancellationTokenSource? _eventCts;

    /// <summary>房间标识。</summary>
    public string RoomId { get; }

    /// <summary>房间名。</summary>
    public string RoomName { get; }

    /// <summary>本机节点 ID（判断自己是不是房主、离开房间时上报）。</summary>
    public string NodeId { get; }

    /// <summary>房主节点 ID（由成员列表刷新，或房主变更事件更新）。</summary>
    [ObservableProperty]
    private string _hostNodeId = string.Empty;

    /// <summary>房主昵称（转让后会变）。</summary>
    [ObservableProperty]
    private string _hostName = string.Empty;

    /// <summary>房间内成员（显示昵称 + 虚拟 IP + 延迟）。</summary>
    public ObservableCollection<RoomPlayerRow> Players { get; } = [];

    /// <summary>状态说明。</summary>
    [ObservableProperty]
    private string _statusText = "正在获取房间成员……";

    /// <summary>是否正在轮询成员/探测延迟。</summary>
    [ObservableProperty]
    private bool _isBusy;

    public RoomPageViewModel(
        string roomId,
        string roomName,
        string hostName,
        string nodeId,
        IRelayServerClient relay,
        IZeroTierService zeroTier,
        IAppNotifier notifier,
        ILogger<RoomPageViewModel> logger,
        Func<Task> leaveRoom,
        Func<string?, Task> roomClosedByServer,
        Func<string> resolveBaseUrl)
    {
        RoomId = roomId;
        RoomName = roomName;
        HostName = hostName;
        NodeId = nodeId;
        _relay = relay;
        _zeroTier = zeroTier;
        _notifier = notifier;
        _logger = logger;
        _leaveRoom = leaveRoom;
        _roomClosedByServer = roomClosedByServer;
        _resolveBaseUrl = resolveBaseUrl;
    }

    public bool HasPlayers => Players.Count > 0;

    /// <summary>自己是不是房主（决定是否显示转让入口）。</summary>
    public bool IsSelfHost =>
        !string.IsNullOrWhiteSpace(HostNodeId)
        && string.Equals(HostNodeId, NodeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>成员数文案。</summary>
    public string PlayerCountText => $"房间成员：{Players.Count} 人";

    /// <summary>房主变了：刷新自己的身份判断与各行的转让按钮可见性。</summary>
    partial void OnHostNodeIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsSelfHost));
        RefreshPromotableFlags();
    }

    /// <summary>启动成员轮询（进入房间页时调用）。</summary>
    public void StartPolling()
    {
        StopPolling();

        _pollCts = new CancellationTokenSource();
        CancellationToken token = _pollCts.Token;

        _ = Task.Run(() => PollLoopAsync(token), token);
    }

    /// <summary>停止成员轮询（退出房间页时调用）。</summary>
    public void StopPolling()
    {
        CancellationTokenSource? cts = _pollCts;
        _pollCts = null;

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>启动服务端事件流订阅（进入房间页时调用）。</summary>
    public void StartEventStream()
    {
        StopEventStream();

        _eventCts = new CancellationTokenSource();
        CancellationToken token = _eventCts.Token;

        _ = Task.Run(() => EventLoopAsync(token), token);
    }

    /// <summary>停止服务端事件流订阅（退出房间页时调用）。</summary>
    public void StopEventStream()
    {
        CancellationTokenSource? cts = _eventCts;
        _eventCts = null;

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>后台循环：周期性拉成员列表 + 逐个探测延迟。</summary>
    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshPlayersAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "轮询房间成员失败。");
            }

            try
            {
                await Task.Delay(POLL_INTERVAL, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 后台循环：维持 SSE 订阅。连接断了就隔几秒重连
    /// （可能是隧道抖动，房间未必没了），直到房间被解散或用户主动离开。
    /// </summary>
    private async Task EventLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                string baseUrl = _resolveBaseUrl();

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    return;
                }

                await _relay.SubscribeRoomEventsAsync(baseUrl, RoomId, OnRoomEventAsync, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "房间事件流中断，{Delay} 秒后重连。", EVENT_RECONNECT_DELAY.TotalSeconds);
            }

            try
            {
                await Task.Delay(EVENT_RECONNECT_DELAY, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>收到一条房间事件：切回 UI 线程再改界面状态（事件是在后台线程读出来的）。</summary>
    private Task OnRoomEventAsync(RelayRoomEvent roomEvent)
        => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => HandleRoomEventAsync(roomEvent));

    private Task HandleRoomEventAsync(RelayRoomEvent roomEvent)
    {
        switch (roomEvent.Type)
        {
            case RelayRoomEventTypes.RoomClosed:
                // 房主退出导致房间解散：不用再上报 leave（房间都没了），直接回大厅。
                _logger.LogInformation("房间 {Room} 已被服务端解散：{Reason}", RoomId, roomEvent.Reason);
                return _roomClosedByServer(roomEvent.Reason);

            case RelayRoomEventTypes.HostChanged:
                if (!string.IsNullOrWhiteSpace(roomEvent.HostNodeId))
                {
                    HostNodeId = roomEvent.HostNodeId!;
                }

                if (!string.IsNullOrWhiteSpace(roomEvent.HostName))
                {
                    HostName = roomEvent.HostName!;
                }

                _notifier.Info("房主已变更", $"房主现在是「{HostName}」。");
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>拉一次成员列表，并对每个成员探测延迟。</summary>
    private async Task RefreshPlayersAsync(CancellationToken token)
    {
        string baseUrl = _resolveBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在刷新房间成员……";

        try
        {
            RelayRoomPlayers room = await _relay.ListPlayersAsync(baseUrl, RoomId, token);

            // 房主可能已被转让：以服务端返回的为准（收到 host_changed 事件时这里是兜底）。
            HostNodeId = room.HostNodeId;
            ApplyPlayers(room.Players);

            // 对每个成员的虚拟 IP 探测延迟。
            await ProbeLatenciesAsync(token);
        }
        catch (RelayServerException ex)
        {
            StatusText = $"刷新成员失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>逐个 Ping 成员虚拟 IP，更新延迟。</summary>
    private async Task ProbeLatenciesAsync(CancellationToken token)
    {
        foreach (RoomPlayerRow row in Players)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(row.VirtualIp))
            {
                row.SetLatencyUnavailable();
                continue;
            }

            try
            {
                ZeroTierPingResult result = await _zeroTier.PingAsync(
                    row.VirtualIp, IZeroTierService.PingPort, PING_TIMEOUT, token);

                row.SetLatency(result.IsSuccess ? result.RoundTripMilliseconds : null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "探测 {Ip} 延迟失败。", row.VirtualIp);
                row.SetLatencyUnavailable();
            }
        }
    }

    /// <summary>把一轮成员结果合并进列表：已知玩家原地更新，离开的移除。
    /// 同一节点（NodeId）的多条服务端记录会合并成一行，避免出现重复用户。</summary>
    private void ApplyPlayers(IReadOnlyList<RelayPlayer> players)
    {
        foreach (RelayPlayer player in players)
        {
            RoomPlayerRow? row = FindPlayer(player.PlayerId) ?? FindByNodeId(player.NodeId);

            if (row is null)
            {
                Players.Add(new RoomPlayerRow(player, TransferHostToAsync));
            }
            else
            {
                row.Update(player);
            }
        }

        for (int index = Players.Count - 1; index >= 0; index--)
        {
            if (!Contains(players, Players[index].PlayerId))
            {
                Players.RemoveAt(index);
            }
        }

        RefreshPromotableFlags();

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayerCountText));
    }

    /// <summary>刷新「可被设为房主」的可见性：只有房主本人能看到其他人的转让按钮。</summary>
    private void RefreshPromotableFlags()
    {
        bool selfIsHost = IsSelfHost;

        foreach (RoomPlayerRow row in Players)
        {
            row.CanBePromoted = selfIsHost && !row.IsHost;
        }
    }

    /// <summary>把房主转让给某个成员（仅房主可发起，服务端会再校验一次）。</summary>
    private async Task TransferHostToAsync(RoomPlayerRow target)
    {
        string baseUrl = _resolveBaseUrl();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        try
        {
            await _relay.TransferHostAsync(baseUrl, RoomId, NodeId, target.NodeId);

            StatusText = $"已把房主转让给「{target.Name}」。";
            _notifier.Success("房主已转让", $"{target.Name} 现在是房主。");

            // 立刻刷新一次，让房主徽章与转让按钮即时同步（不用等下一轮轮询）。
            await RefreshPlayersAsync(CancellationToken.None);
        }
        catch (RelayServerException ex)
        {
            StatusText = $"转让房主失败：{ex.Message}";
            _notifier.Warning("转让房主失败", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "转让房主失败。");
            _notifier.Error("转让房主失败", ex.Message);
        }
    }

    private RoomPlayerRow? FindPlayer(Guid playerId)
    {
        foreach (RoomPlayerRow row in Players)
        {
            if (row.PlayerId == playerId)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>按 ZeroTier 节点 ID 找已有行（服务端遗留的同一节点多条记录合并为一行）。</summary>
    private RoomPlayerRow? FindByNodeId(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        foreach (RoomPlayerRow row in Players)
        {
            if (string.Equals(row.NodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    private static bool Contains(IReadOnlyList<RelayPlayer> players, Guid playerId)
    {
        foreach (RelayPlayer player in players)
        {
            if (player.PlayerId == playerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>退出房间：停止轮询与事件流，并回调 RoomsViewModel 执行离开。</summary>
    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        StopPolling();
        StopEventStream();

        try
        {
            await _leaveRoom();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "退出房间失败。");
            _notifier.Error("退出房间失败", ex.Message);
        }
    }
}

/// <summary>房间内成员列表里的一行（显示昵称 + 虚拟 IP + 延迟，房主带徽章）。</summary>
public sealed partial class RoomPlayerRow : ObservableObject
{
    public Guid PlayerId { get; private set; }

    /// <summary>成员的 ZeroTier 节点 ID（同一节点的多条服务端记录在界面上合并为一行）。</summary>
    public string NodeId { get; private set; } = string.Empty;

    /// <summary>成员昵称。</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>成员在 ZeroTier 虚拟网中的 IP（同时用作延迟探测目标）。</summary>
    public string VirtualIp { get; private set; } = string.Empty;

    /// <summary>是不是房主（界面显示房主徽章）。</summary>
    [ObservableProperty]
    private bool _isHost;

    /// <summary>能不能被设为房主（自己是房主且这一行不是我时，显示转让按钮）。</summary>
    [ObservableProperty]
    private bool _canBePromoted;

    /// <summary>延迟（毫秒）；null 表示探测失败/不可用。</summary>
    [ObservableProperty]
    private long? _latencyMilliseconds;

    /// <summary>把房主转让给这一行（由 RoomPageViewModel 注入）。</summary>
    public IRelayCommand TransferHostCommand { get; }

    /// <summary>延迟显示文本（「23 ms」或「—」）。</summary>
    public string LatencyText => LatencyMilliseconds.HasValue ? $"{LatencyMilliseconds} ms" : "—";

    public RoomPlayerRow(RelayPlayer player, Func<RoomPlayerRow, Task> transferRequested)
    {
        TransferHostCommand = new AsyncRelayCommand(() => transferRequested(this));
        Update(player);
    }

    partial void OnLatencyMillisecondsChanged(long? value) => OnPropertyChanged(nameof(LatencyText));

    public void Update(RelayPlayer player)
    {
        PlayerId = player.PlayerId;
        NodeId = player.NodeId;
        Name = string.IsNullOrWhiteSpace(player.Nickname) ? "未命名玩家" : player.Nickname;
        VirtualIp = player.VirtualIp;
        IsHost = player.IsHost;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VirtualIp));
    }

    /// <summary>标记延迟为成功探测到的值（null 表示不可达）。</summary>
    public void SetLatency(long? milliseconds) => LatencyMilliseconds = milliseconds;

    /// <summary>标记延迟不可用（探测失败）。</summary>
    public void SetLatencyUnavailable() => LatencyMilliseconds = null;
}
