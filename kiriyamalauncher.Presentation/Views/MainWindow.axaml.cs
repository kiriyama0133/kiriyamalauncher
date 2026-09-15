using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using kiriyamalauncher.Presentation.Base;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using kiriyamalauncher.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Views;

public partial class MainWindow : SukiWindow
{
    /// <summary>关闭清理是否已完成（完成后再真正关闭，避免重复拦截）。</summary>
    private bool _closeCleanupDone;

    public MainWindow()
    {
        InitializeComponent();

        // 标题栏上的外观面板需要绑定主 ViewModel。
        this.DataContext = Ioc.Default.GetRequiredService<MainViewModel>();

        // SukiWindow 的标题栏由 SukiUI 自己绘制，所以各平台都显示应用名。
        this.Title = AppInfo.AppDisplayName;

        // 用已恢复（或默认）的字号初始化窗口；偏好恢复完成后快照会再套用一次。
        this.FontSize = Ioc.Default.GetRequiredService<IAppSnapshot>().Preferences.FontSize;

        // 关闭前检查：Client 引擎还挂着虚拟网卡时，先清理再真正退出，并给 toast 提示。
        this.Closing += OnWindowClosing;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 已经做过关闭清理（二次进入说明是清理完成后我们自己调的 Close），直接放行。
        if (_closeCleanupDone)
        {
            return;
        }

        IZeroTierService zeroTier = Ioc.Default.GetRequiredService<IZeroTierService>();

        // 只有「嵌入的 ZeroTier 客户端」引擎才有系统虚拟网卡（libzt 是进程内，随进程退出，无需清理）。
        bool isClientBackend = string.Equals(zeroTier.Kind, ZeroTierSettings.ClientBackend, StringComparison.OrdinalIgnoreCase);
        bool hasJoinedNetworks = zeroTier.JoinedNetworkIds.Count > 0;

        if (!isClientBackend || !hasJoinedNetworks)
        {
            return; // 没有需要清理的虚拟网卡，直接正常关闭（OnDesktopExit 里还有兜底）。
        }

        // 有虚拟网卡未清理：取消本次关闭，先异步清理，完成后自己再关。
        e.Cancel = true;

        IAppNotifier notifier = Ioc.Default.GetRequiredService<IAppNotifier>();
        ILogger<MainWindow>? logger = Ioc.Default.GetService<ILogger<MainWindow>>();

        using (IDisposable loading = notifier.ShowLoading("正在清理虚拟网卡", "正在离开 ZeroTier 网络并移除虚拟网卡……"))
        {
            try
            {
                await Task.WhenAll(
                    Ioc.Default.GetServices<IZeroTierBackend>().Select(backend => backend.CleanupOnExitAsync()));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "关闭前清理 ZeroTier 失败（忽略）。");
            }
        }

        notifier.Success("虚拟网卡已清理", "已离开全部 ZeroTier 网络，正在退出应用。");

        // 清理完成：置位放行标志后再次触发关闭。
        _closeCleanupDone = true;
        Close();
    }
}
