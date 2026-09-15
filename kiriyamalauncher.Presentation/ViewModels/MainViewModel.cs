using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Presentation.Base;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI.Toasts;
using SukiUI.Dialogs;
using Material.Icons;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

namespace kiriyamalauncher.Presentation.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    public string AppDisplayName { get; }

    public string VersionText { get; }

    /// <summary>标题栏外观面板使用的设置。</summary>
    public AppearanceViewModel Appearance { get; }

    /// <summary>右下角 toast 宿主需要绑定的管理器。</summary>
    public ISukiToastManager ToastManager { get; }

    /// <summary>对话框宿主需要绑定的管理器。</summary>
    public ISukiDialogManager DialogManager { get; }

    /// <summary>是否已登录（用昵称判断）。</summary>
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_snapshot.User.Nickname);

    /// <summary>标题栏显示的文字：未登录显示「登录」，登录后显示昵称。</summary>
    public string AccountText => IsSignedIn ? _snapshot.User.Nickname : "登录";

    private readonly IAppSnapshot _snapshot;
    private readonly IAppNotifier _notifier;

    /// <summary>窗口是否固定在最上层（标题栏右侧的图钉按钮）。</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>图钉按钮的图标。</summary>
    public MaterialIconKind PinIcon => IsPinned ? MaterialIconKind.Pin : MaterialIconKind.PinOff;

    /// <summary>导航栏中的页面。</summary>
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel? _activePage;

    [ObservableProperty]
    private bool _isMenuExpanded;

    public MainViewModel()
    {
        AppDisplayName = AppInfo.AppDisplayName;
        VersionText = $"版本 {AppInfo.Version}";
        Appearance = Ioc.Default.GetRequiredService<AppearanceViewModel>();
        ToastManager = Ioc.Default.GetRequiredService<ISukiToastManager>();
        DialogManager = Ioc.Default.GetRequiredService<ISukiDialogManager>();
        _snapshot = Ioc.Default.GetRequiredService<IAppSnapshot>();
        _notifier = Ioc.Default.GetRequiredService<IAppNotifier>();
        _snapshot.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(AccountText));
        };

        Pages = new ObservableCollection<PageViewModel>
        {
            Ioc.Default.GetRequiredService<HomeViewModel>(),
            Ioc.Default.GetRequiredService<GameSessionViewModel>(),
            Ioc.Default.GetRequiredService<SettingsViewModel>(),
            Ioc.Default.GetRequiredService<LogsViewModel>()
        };

        ActivePage = Pages[0];
        IsMenuExpanded = true;
    }

    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(PinIcon));

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    /// <summary>打开登录对话框。</summary>
    [RelayCommand]
    private void OpenSignIn()
    {
        DialogManager.CreateDialog()
            .WithViewModel(dialog => new SignInDialogViewModel(
                dialog,
                _snapshot,
                Ioc.Default.GetRequiredService<Base.Services.Notifications.IAppNotifier>()))
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    /// <summary>打开「网络工具」对话框（虚拟局域网设备发现 + Ping 探测）。</summary>
    [RelayCommand]
    private void OpenNetworkTools()
    {
        DialogManager.CreateDialog()
            .WithViewModel(dialog => new NetworkToolsDialogViewModel(
                dialog,
                Ioc.Default.GetRequiredService<IZeroTierService>(),
                Ioc.Default.GetRequiredService<IVirtualLanDiscoveryService>(),
                Ioc.Default.GetRequiredService<IAppNotifier>(),
                Ioc.Default.GetRequiredService<ILogger<NetworkToolsDialogViewModel>>()))
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    [RelayCommand]
    private async Task SignOut()
    {
        // 退出前先记住昵称，退出后账号信息就清空了。
        string nickname = AccountText;

        await _snapshot.SignOutAsync();

        // 退出登录后，需要鉴权的页面（游戏联机/房间大厅/房间内）都已失效，
        // 把活动页切回首页，避免用户停留在失效状态。
        ActivePage = Pages[0];

        _notifier.Info("已退出登录", $"你已成功退出账户{nickname}");
    }
}
