using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 房间内页面：显示房间内成员（只显示名称），并自动轮询探测每个成员的延迟。
/// 由 <see cref="RoomsViewModel"/> 在加入房间后创建并挂到房间大厅的切换内容里。
/// </summary>
public sealed partial class RoomPageViewModel : BaseViewModel
{
    /// <summary>成员延迟轮询间隔。</summary>
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(5);

    /// <summary>单次 Ping 的超时。</summary>
    private static readonly TimeSpan PING_TIMEOUT = TimeSpan.FromSeconds(3);

    private readonly IRelayServerClient _relay;
    private readonly IZeroTierService _zeroTier;
    private readonly IAppNotifier _notifier;
    private readonly ILogger<RoomPageViewModel> _logger;

    /// <summary>成员延迟探测的后台循环（退出房间时取消）。</summary>
    private CancellationTokenSource? _pollCts;

    /// <summary>房间标识。</summary>
    public string RoomId { get; }

    /// <summary>房间名。</summary>
    public string RoomName { get; }

    /// <summary>房主名称。</summary>
    public string HostName { get; }

    /// <summary>本机节点 ID（离开房间时上报，服务端据此清除 Tag）。</summary>
    public string NodeId { get; }

    /// <summary>房间内成员（只显示名称 + 延迟）。</summary>
    public ObservableCollection<RoomPlayerRow> Players { get; } = [];

    /// <summary>状态说明。</summary>
    [ObservableProperty]
    private string _statusText = "正在获取房间成员……";

    /// <summary>是否正在轮询成员/探测延迟。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>退出房间的回调（由 RoomsViewModel 提供）。</summary>
    private readonly Func<Task> _leaveRoom;

    /// <summary>解析中继服务器 baseUrl 的委托（由 RoomsViewModel 提供）。</summary>
    private readonly Func<string> _resolveBaseUrl;

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
        _resolveBaseUrl = resolveBaseUrl;
    }

    public bool HasPlayers => Players.Count > 0;

    /// <summary>成员数文案。</summary>
    public string PlayerCountText => $"房间成员：{Players.Count} 人";

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
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
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

            // 更新成员列表（只显示名称；延迟在下一步探测）。
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

    /// <summary>把一轮成员结果合并进列表：已知玩家原地更新，离开的移除。</summary>
    private void ApplyPlayers(System.Collections.Generic.IReadOnlyList<RelayPlayer> players)
    {
        foreach (RelayPlayer player in players)
        {
            RoomPlayerRow? row = FindPlayer(player.PlayerId);

            if (row is null)
            {
                Players.Add(new RoomPlayerRow(player));
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

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayerCountText));
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

    private static bool Contains(System.Collections.Generic.IReadOnlyList<RelayPlayer> players, Guid playerId)
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

    /// <summary>退出房间：停止轮询并回调 RoomsViewModel 执行离开。</summary>
    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        StopPolling();

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

/// <summary>房间内成员列表里的一行（只显示名称 + 延迟）。</summary>
public sealed partial class RoomPlayerRow : ObservableObject
{
    public Guid PlayerId { get; private set; }

    /// <summary>成员名称（界面只显示这个）。</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>成员虚拟 IP（延迟探测目标，不展示给用户）。</summary>
    public string VirtualIp { get; private set; } = string.Empty;

    /// <summary>延迟（毫秒）；null 表示探测失败/不可用。</summary>
    [ObservableProperty]
    private long? _latencyMilliseconds;

    /// <summary>延迟显示文本（「23 ms」或「—」）。</summary>
    public string LatencyText => LatencyMilliseconds.HasValue ? $"{LatencyMilliseconds} ms" : "—";

    public RoomPlayerRow(RelayPlayer player)
    {
        Update(player);
    }

    partial void OnLatencyMillisecondsChanged(long? value) => OnPropertyChanged(nameof(LatencyText));

    public void Update(RelayPlayer player)
    {
        PlayerId = player.PlayerId;
        Name = string.IsNullOrWhiteSpace(player.Nickname) ? "未命名玩家" : player.Nickname;
        VirtualIp = player.VirtualIp;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VirtualIp));
    }

    /// <summary>标记延迟为成功探测到的值（null 表示不可达）。</summary>
    public void SetLatency(long? milliseconds) => LatencyMilliseconds = milliseconds;

    /// <summary>标记延迟不可用（探测失败）。</summary>
    public void SetLatencyUnavailable() => LatencyMilliseconds = null;
}
