using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI.Dialogs;
using System;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>「创建房间」对话框：输入房间名与可选密码。创建成功后回调（房主自动进入房间）。</summary>
public partial class CreateRoomDialogViewModel : BaseViewModel
{
    private readonly ISukiDialog _dialog;
    private readonly IRelayServerClient _relay;
    private readonly string _baseUrl;
    private readonly string _gameKey;
    private readonly string _hostName;
    private readonly IAppNotifier _notifier;
    private readonly Action<RelayRoom, string?> _onCreated;

    [ObservableProperty]
    private string _roomName = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private string? _error;

    public CreateRoomDialogViewModel(
        ISukiDialog dialog,
        IRelayServerClient relay,
        string baseUrl,
        string gameKey,
        string hostName,
        IAppNotifier notifier,
        Action<RelayRoom, string?> onCreated)
    {
        _dialog = dialog;
        _relay = relay;
        _baseUrl = baseUrl;
        _gameKey = gameKey;
        _hostName = hostName;
        _notifier = notifier;
        _onCreated = onCreated;
    }

    partial void OnRoomNameChanged(string value) => Error = null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        string name = RoomName.Trim();
        string? password = string.IsNullOrWhiteSpace(Password) ? null : Password;

        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "请填写房间名。";
            return;
        }

        IsSubmitting = true;
        Error = null;

        try
        {
            RelayRoom room = await _relay.CreateRoomAsync(_baseUrl, name, _hostName, _gameKey, password);
            _notifier.Success("房间已创建", $"「{name}」已经上线，正在进入房间……");
            _dialog.Dismiss();

            // 房主创建后直接进入房间（自动 join，带上创建时用的密码）。
            _onCreated(room, password);
        }
        catch (RelayServerException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = $"创建失败：{ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}

/// <summary>「加入房间」对话框：输入房间密码。</summary>
public partial class JoinRoomDialogViewModel : BaseViewModel
{
    private readonly ISukiDialog _dialog;
    private readonly IRelayServerClient _relay;
    private readonly string _baseUrl;
    private readonly string _nickname;
    private readonly string _nodeId;
    private readonly string _virtualIp;
    private readonly IAppNotifier _notifier;
    private readonly RoomRowViewModel _room;
    private readonly Action _onJoined;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private string? _error;

    public string RoomName => _room.Name;

    public JoinRoomDialogViewModel(
        ISukiDialog dialog,
        IRelayServerClient relay,
        string baseUrl,
        string nickname,
        string nodeId,
        string virtualIp,
        IAppNotifier notifier,
        RoomRowViewModel room,
        Action onJoined)
    {
        _dialog = dialog;
        _relay = relay;
        _baseUrl = baseUrl;
        _nickname = nickname;
        _nodeId = nodeId;
        _virtualIp = virtualIp;
        _notifier = notifier;
        _room = room;
        _onJoined = onJoined;
    }

    partial void OnPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        Error = null;

        try
        {
            await _relay.JoinRoomAsync(_baseUrl, _room.Id, _nickname, _nodeId, _virtualIp, Password);
            _notifier.Success("已加入房间", $"欢迎来到「{_room.Name}」。");
            _dialog.Dismiss();
            _onJoined();
        }
        catch (RelayServerException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = $"加入失败：{ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}
