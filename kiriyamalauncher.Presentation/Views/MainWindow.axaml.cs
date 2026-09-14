using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Presentation.Base;
using kiriyamalauncher.Presentation.ViewModels;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using SukiUI.Controls;

namespace kiriyamalauncher.Presentation.Views;

public partial class MainWindow : SukiWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // 标题栏上的外观面板需要绑定主 ViewModel。
        this.DataContext = Ioc.Default.GetRequiredService<MainViewModel>();

        // SukiWindow 的标题栏由 SukiUI 自己绘制，所以各平台都显示应用名。
        this.Title = AppInfo.AppDisplayName;

        // 用已恢复（或默认）的字号初始化窗口；偏好恢复完成后快照会再套用一次。
        this.FontSize = Ioc.Default.GetRequiredService<IAppSnapshot>().Preferences.FontSize;
    }
}
