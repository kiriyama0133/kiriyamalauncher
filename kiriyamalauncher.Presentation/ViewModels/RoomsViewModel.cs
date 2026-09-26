using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using Microsoft.Extensions.Logging;
using SukiUI.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 房间大厅：连到中继服务器（服务端联机），列出 / 搜索 / 创建 / 加入房间。
/// 挂在游戏板块里，只有「中继服务器」连接模式下才显示。
/// </summary>
public partial class RoomsViewModel : ObservableObject
{
    /// <summary>没有选定板块时的兜底游戏标识（与文明 6 的集成标识一致）。</summary>
    private const string DEFAULT_GAME_KEY = "civ6";

    private readonly IRelayServerClient _relay;
    private readonly IAppSnapshot _snapshot;
    private readonly IAppNotifier _notifier;
    private readonly ISukiDialogManager _dialogManager;
    private readonly IZeroTierService _zeroTier;
    private readonly ILogger<RoomsViewModel> _logger;
    private readonly ILogger<RoomPageViewModel> _roomLogger;

    private IReadOnlyList<RelayRoom> _allRooms = [];

    /// <summary>
    /// 当前游戏板块的标识（如 civ6 / mc）。进入板块时由 GameSessionViewModel 设置：
    /// 创建房间时上报给服务端、房间列表按它过滤。为空表示不按板块过滤（显示全部）。
    /// </summary>
    private string? _activeGameKey;

    /// <summary>房间列表（已按搜索词过滤）。</summary>
    public ObservableCollection<RoomRowViewModel> Rooms { get; } = [];

    /// <summary>状态说明。</summary>
    [ObservableProperty]
    private string _statusText = "尚未连接中继服务器。";

    /// <summary>是否正在连接 / 刷新。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>是否已经连上中继服务器。</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>搜索关键字（本地过滤房间名）。</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>当前选中的房间。</summary>
    [ObservableProperty]
    private RoomRowViewModel? _selectedRoom;

    /// <summary>当前所在的房间页面；为 null 表示在房间大厅。</summary>
    [ObservableProperty]
    private RoomPageViewModel? _currentRoom;

    /// <summary>是否已经进入某个房间（房间内页面）。</summary>
    public bool IsInRoom => CurrentRoom is not null;

    public RoomsViewModel(
        IRelayServerClient relay,
        IAppSnapshot snapshot,
        IAppNotifier notifier,
        ISukiDialogManager dialogManager,
        IZeroTierService zeroTier,
        ILogger<RoomsViewModel> logger,
        ILogger<RoomPageViewModel> roomLogger)
    {
        _relay = relay;
        _snapshot = snapshot;
        _notifier = notifier;
        _dialogManager = dialogManager;
        _zeroTier = zeroTier;
        _logger = logger;
        _roomLogger = roomLogger;
    }

    /// <summary>是否有房间。</summary>
    public bool HasRooms => Rooms.Count > 0;

    /// <summary>在线房间数（过滤后的可见房间数）。</summary>
    public int OnlineRoomCount => Rooms.Count;

    /// <summary>是否正在搜索（用于显示「清除搜索」）。</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(IsSearching));
    }

    partial void OnCurrentRoomChanged(RoomPageViewModel? value) => OnPropertyChanged(nameof(IsInRoom));

    /// <summary>连接中继服务器并拉取房间列表。</summary>
    public Task ConnectAsync() => LoadRoomsAsync();

    /// <summary>
    /// 设置当前所在的游戏板块（进入板块时调用）。板块变了就清空旧列表并按新板块重新拉取，
    /// 否则在 Minecraft 板块里会看到文明 6 的房间。
    /// </summary>
    public void SetActiveGame(string? gameKey)
    {
        string normalized = (gameKey ?? string.Empty).Trim();

        if (string.Equals(_activeGameKey, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeGameKey = normalized.Length == 0 ? null : normalized;
        _allRooms = [];
        ApplyFilter();

        if (IsConnected)
        {
            _ = LoadRoomsAsync();
        }
    }

    /// <summary>创建房间时上报的游戏板块标识（没有进入具体板块时退回 civ6）。</summary>
    private string ActiveGameKey => string.IsNullOrWhiteSpace(_activeGameKey) ? DEFAULT_GAME_KEY : _activeGameKey;

    /// <summary>房主昵称（用于创建房间时上报给服务器）。</summary>
    private string HostName =>
        string.IsNullOrWhiteSpace(_snapshot.User.Nickname)
            ? _snapshot.User.Email
            : _snapshot.User.Nickname;

    /// <summary>校验是否已登录；未登录则 toast 提醒并返回 false。</summary>
    private bool EnsureSignedIn()
    {
        if (_snapshot.User.IsSignedIn)
        {
            return true;
        }

        _notifier.Warning("请先登录", "登录后才能连接局域网和创建房间。");
        return false;
    }

    /// <summary>
    /// 从 ZeroTier 状态解析本机身份（节点 ID + 虚拟 IP）。
    /// 服务端加入房间时需要这两项给节点打房间 Tag；拿不到说明还没接入 ZeroTier 网络。
    /// </summary>
    private bool TryGetZeroTierIdentity(out string nodeId, out string virtualIp)
    {
        nodeId = string.Empty;
        virtualIp = string.Empty;

        // 先遍历已加入的网络：虚拟 IP 一定是网络级别的，节点 ID 是节点级别的。
        foreach (ulong networkId in _zeroTier.JoinedNetworkIds)
        {
            ZeroTierStatus status = _zeroTier.GetStatus(networkId);

            if (string.IsNullOrWhiteSpace(nodeId) && !string.IsNullOrWhiteSpace(status.NodeId))
            {
                nodeId = status.NodeId;
            }

            if (string.IsNullOrWhiteSpace(virtualIp) && !string.IsNullOrWhiteSpace(status.VirtualIp))
            {
                virtualIp = status.VirtualIp;
            }
        }

        // 兜底：两个后端的 GetStatus(0) 都安全返回节点 ID（不含虚拟 IP）。
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            nodeId = _zeroTier.GetStatus(0).NodeId;
        }

        if (string.IsNullOrWhiteSpace(virtualIp))
        {
            virtualIp = _zeroTier.LocalVirtualIp;
        }

        return !string.IsNullOrWhiteSpace(nodeId) && !string.IsNullOrWhiteSpace(virtualIp);
    }

    /// <summary>重新拉取房间列表。</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadRoomsAsync();

    /// <summary>清空搜索词。</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>断开中继服务器：清空房间列表与状态（应用层断开，不涉及真实连接）。</summary>
    public void Disconnect()
    {
        // 如果还在房间内，先停轮询与事件流，再清掉房间页面状态。
        if (CurrentRoom is not null)
        {
            CurrentRoom.StopPolling();
            CurrentRoom.StopEventStream();
            CurrentRoom = null;
        }

        _allRooms = [];
        SearchText = string.Empty;
        ApplyFilter();
        IsConnected = false;
        IsBusy = false;
        StatusText = "已断开中继服务器。";
    }

    /// <summary>创建房间：弹出对话框输入房间名与密码。</summary>
    [RelayCommand]
    private void CreateRoom()
    {
        if (!EnsureSignedIn())
        {
            return;
        }

        string baseUrl = ResolveBaseUrl();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            StatusText = "请先在设置里填写中继服务器地址。";
            return;
        }

        // 创建房间要上报房主节点 ID（服务端据此认定房主，房主退出时解散房间），
        // 所以在打开对话框之前先把本机 ZeroTier 身份解析出来。
        if (!TryGetZeroTierIdentity(out string nodeId, out _))
        {
            StatusText = "尚未接入 ZeroTier 网络，无法创建房间。";
            _notifier.Warning(
                "尚未接入 ZeroTier 网络",
                "创建房间前，请先点击右上角「连接到虚拟局域网」接入中继服务器的 ZeroTier 网络。");
            return;
        }

        _dialogManager.CreateDialog()
            .WithViewModel(dialog => new CreateRoomDialogViewModel(
                dialog,
                _relay,
                baseUrl,
                ActiveGameKey,
                HostName,
                nodeId,
                _notifier,
                (room, password) => _ = JoinAsync(room.Id, room.Name, room.HostName, password)))
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    /// <summary>加入房间：无密码直接加入，有密码弹出密码输入框。</summary>
    [RelayCommand]
    private void JoinRoom(RoomRowViewModel? room)
    {
        if (room is null || IsBusy)
        {
            return;
        }

        if (!EnsureSignedIn())
        {
            return;
        }

        string baseUrl = ResolveBaseUrl();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            StatusText = "请先在设置里填写中继服务器地址。";
            return;
        }

        // 服务端加入房间需要给节点打 Tag：身份（节点 ID / 虚拟 IP）在打开对话框前就解析好。
        if (!TryGetZeroTierIdentity(out string nodeId, out string virtualIp))
        {
            StatusText = "尚未接入 ZeroTier 网络，无法进入房间。";
            _notifier.Warning(
                "尚未接入 ZeroTier 网络",
                "进入房间前，请先点击右上角「连接到虚拟局域网」接入中继服务器的 ZeroTier 网络（需要拿到节点 ID 与虚拟 IP，服务端才能给本机打房间 Tag）。");
            return;
        }

        if (!room.HasPassword)
        {
            _ = JoinAsync(room.Id, room.Name, room.HostName, null, nodeId, virtualIp);
            return;
        }

        _dialogManager.CreateDialog()
            .WithViewModel(dialog => new JoinRoomDialogViewModel(
                dialog,
                _relay,
                baseUrl,
                HostName,
                nodeId,
                virtualIp,
                _notifier,
                room,
                () => _ = EnterRoomAsync(room.Id, room.Name, room.HostName, nodeId)))
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    private async Task JoinAsync(string roomId, string roomName, string hostName, string? password, string? nodeId = null, string? virtualIp = null)
    {
        IsBusy = true;
        StatusText = $"正在加入房间「{roomName}」……";

        try
        {
            string baseUrl = ResolveBaseUrl();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                StatusText = "请先在设置里填写中继服务器地址。";
                return;
            }

            // 服务端加入房间需要给节点打 Tag，必须带本机 ZeroTier 身份。
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(virtualIp))
            {
                if (!TryGetZeroTierIdentity(out nodeId!, out virtualIp!))
                {
                    IsConnected = true;
                    StatusText = "尚未接入 ZeroTier 网络，无法进入房间。";
                    _notifier.Warning(
                        "尚未接入 ZeroTier 网络",
                        "进入房间前，请先点击右上角「连接到虚拟局域网」接入中继服务器的 ZeroTier 网络（需要拿到节点 ID 与虚拟 IP，服务端才能给本机打房间 Tag）。");
                    return;
                }
            }

            await _relay.JoinRoomAsync(baseUrl, roomId, HostName, nodeId, virtualIp, password);
            IsConnected = true;

            await EnterRoomAsync(roomId, roomName, hostName, nodeId);
        }
        catch (RelayServerException ex)
        {
            StatusText = $"加入房间失败：{ex.Message}";
            _notifier.Warning("加入房间失败", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加入房间「{Room}」失败。", roomName);
            StatusText = $"加入房间失败：{ex.Message}";
            _notifier.Error("加入房间失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 进入房间页面：创建 RoomPageViewModel 并启动成员轮询。
    /// 无密码房间在 JoinAsync 里调用；有密码房间由 JoinRoomDialogViewModel 的
    /// 成功回调调用（加入已在对话框里完成，这里只负责跳转房间页）。
    /// </summary>
    private Task EnterRoomAsync(string roomId, string roomName, string hostName, string nodeId)
    {
        CurrentRoom = new RoomPageViewModel(
            roomId,
            roomName,
            hostName,
            nodeId,
            _relay,
            _zeroTier,
            _notifier,
            _roomLogger,
            LeaveCurrentRoomAsync,
            ExitClosedRoomAsync,
            ResolveBaseUrl);

        StatusText = $"已进入房间「{roomName}」。";
        _notifier.Success("已进入房间", $"欢迎来到「{roomName}」。");

        CurrentRoom.StartPolling();

        // 同时订阅服务端事件流：房主退出解散房间时会被服务端主动请出（无需等轮询）。
        CurrentRoom.StartEventStream();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 被动退出：服务端推来 room_closed（房主退出导致房间解散）时直接回大厅。
    /// 与主动退房的关键区别是<b>不再上报 leave</b> —— 房间都已经不存在了，再上报只会拿到 404。
    /// </summary>
    private async Task ExitClosedRoomAsync(string? reason)
    {
        RoomPageViewModel? room = CurrentRoom;
        if (room is null)
        {
            return;
        }

        room.StopPolling();
        room.StopEventStream();
        CurrentRoom = null;

        string message = string.IsNullOrWhiteSpace(reason) ? "房间已解散。" : reason;
        StatusText = $"{message}已回到房间大厅。";
        _notifier.Warning("已离开房间", message);

        await LoadRoomsAsync();
    }

    /// <summary>离开当前房间：上报服务端清除 Tag，停止轮询，回到大厅。</summary>
    private async Task LeaveCurrentRoomAsync()
    {
        RoomPageViewModel? room = CurrentRoom;
        if (room is null)
        {
            return;
        }

        string baseUrl = ResolveBaseUrl();

        // 先停轮询与事件流，避免离开过程中还在探测或收事件。
        room.StopPolling();
        room.StopEventStream();

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            try
            {
                await _relay.LeaveRoomAsync(baseUrl, room.RoomId, room.NodeId);
            }
            catch (RelayServerException ex)
            {
                _notifier.Warning("离开房间时出错", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "离开房间「{Room}」失败。", room.RoomName);
            }
        }

        CurrentRoom = null;
        StatusText = "已离开房间，回到房间大厅。";
        _notifier.Info("已离开房间", $"你已退出「{room.RoomName}」。");

        // 回到大厅后刷新房间列表（成员数可能已变化）。
        await LoadRoomsAsync();
    }

    /// <summary>拉取房间列表（供连接 / 刷新 / 创建后 / 加入后复用）。</summary>
    private async Task LoadRoomsAsync()
    {
        if (!EnsureSignedIn())
        {
            return;
        }

        string baseUrl = ResolveBaseUrl();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            StatusText = "请先在设置里填写中继服务器地址。";
            return;
        }

        IsBusy = true;
        StatusText = "正在连接中继服务器……";

        try
        {
            // 按当前板块拉取：房间大厅只显示本板块的房间。
            IReadOnlyList<RelayRoom> rooms = string.IsNullOrWhiteSpace(_activeGameKey)
                ? await _relay.ListRoomsAsync(baseUrl)
                : await _relay.ListRoomsAsync(baseUrl, _activeGameKey);

            _allRooms = rooms;
            ApplyFilter();
            IsConnected = true;
            StatusText = $"已连接，在线房间 {rooms.Count} 个。";
        }
        catch (RelayServerException ex)
        {
            IsConnected = false;
            StatusText = $"连接失败：{ex.Message}";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            _logger.LogError(ex, "拉取房间列表失败。");
            StatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按搜索词过滤房间列表（空搜索词显示全部）。</summary>
    private void ApplyFilter()
    {
        Rooms.Clear();

        foreach (RelayRoom room in _allRooms)
        {
            if (string.IsNullOrWhiteSpace(SearchText)
                || room.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                Rooms.Add(new RoomRowViewModel(room, row => JoinRoom(row)));
            }
        }

        OnPropertyChanged(nameof(HasRooms));
        OnPropertyChanged(nameof(OnlineRoomCount));
    }

    /// <summary>从偏好拼出中继服务器 baseUrl；没配置时返回空串。</summary>
    internal string ResolveBaseUrl()
    {
        string ip = (_snapshot.Preferences.RelayServerIp ?? string.Empty).Trim();
        int port = _snapshot.Preferences.RelayServerPort;

        if (string.IsNullOrWhiteSpace(ip))
        {
            return string.Empty;
        }

        if (ip.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || ip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ip.TrimEnd('/');
        }

        return $"http://{ip}:{port}";
    }
}

/// <summary>房间列表里的一行（包装 <see cref="RelayRoom"/> 并带加入命令）。</summary>
public partial class RoomRowViewModel : ObservableObject
{
    private readonly Action<RoomRowViewModel> _joinRequested;

    public RoomRowViewModel(RelayRoom room, Action<RoomRowViewModel> joinRequested)
    {
        Room = room;
        _joinRequested = joinRequested;
    }

    public RelayRoom Room { get; }

    public string Id => Room.Id;

    public string Name => Room.Name;

    public string HostName => Room.HostName;

    public string PlayerCountText => Room.PlayerCountText;

    public bool HasPassword => Room.HasPassword;

    /// <summary>给界面看的密码状态文本（「有密码」/「无密码」）。</summary>
    public string PasswordText => Room.HasPassword ? "有密码" : "无密码";

    /// <summary>房间是否已满。</summary>
    public bool IsFull => Room.MaxPlayers > 0 && Room.PlayerCount >= Room.MaxPlayers;

    [RelayCommand]
    private void Join() => _joinRequested(this);
}
