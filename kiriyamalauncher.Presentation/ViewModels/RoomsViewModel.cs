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
    private readonly IRelayServerClient _relay;
    private readonly IAppSnapshot _snapshot;
    private readonly IAppNotifier _notifier;
    private readonly ISukiDialogManager _dialogManager;
    private readonly IZeroTierService _zeroTier;
    private readonly ILogger<RoomsViewModel> _logger;

    private IReadOnlyList<RelayRoom> _allRooms = [];

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

    public RoomsViewModel(
        IRelayServerClient relay,
        IAppSnapshot snapshot,
        IAppNotifier notifier,
        ISukiDialogManager dialogManager,
        IZeroTierService zeroTier,
        ILogger<RoomsViewModel> logger)
    {
        _relay = relay;
        _snapshot = snapshot;
        _notifier = notifier;
        _dialogManager = dialogManager;
        _zeroTier = zeroTier;
        _logger = logger;
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

    /// <summary>连接中继服务器并拉取房间列表。</summary>
    public Task ConnectAsync() => LoadRoomsAsync();

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

        _dialogManager.CreateDialog()
            .WithViewModel(dialog => new CreateRoomDialogViewModel(
                dialog,
                _relay,
                baseUrl,
                "civ6",
                HostName,
                _notifier,
                (room, password) => _ = JoinAsync(room.Id, room.Name, password)))
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
                "进入房间前，请先连接到中继服务器的 ZeroTier 网络（需要拿到节点 ID 与虚拟 IP）。\n\n提示：在设置里选择「自建控制器」模式并填入服务器网络 ID，然后连接虚拟局域网。");
            return;
        }

        if (!room.HasPassword)
        {
            _ = JoinAsync(room.Id, room.Name, null, nodeId, virtualIp);
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
                () => _ = LoadRoomsAsync()))
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    private async Task JoinAsync(string roomId, string roomName, string? password, string? nodeId = null, string? virtualIp = null)
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
                        "进入房间前，请先连接到中继服务器的 ZeroTier 网络（需要拿到节点 ID 与虚拟 IP）。\n\n提示：在设置里选择「自建控制器」模式并填入服务器网络 ID，然后连接虚拟局域网。");
                    return;
                }
            }

            await _relay.JoinRoomAsync(baseUrl, roomId, HostName, nodeId, virtualIp, password);
            IsConnected = true;
            StatusText = $"已进入房间「{roomName}」（虚拟 IP：{virtualIp}）。";
            _notifier.Success("已进入房间", $"欢迎来到「{roomName}」，虚拟 IP：{virtualIp}。");
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
            IReadOnlyList<RelayRoom> rooms = await _relay.ListRoomsAsync(baseUrl);
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

    /// <summary>房间是否已满。</summary>
    public bool IsFull => Room.MaxPlayers > 0 && Room.PlayerCount >= Room.MaxPlayers;

    [RelayCommand]
    private void Join() => _joinRequested(this);
}
