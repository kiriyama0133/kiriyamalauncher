using Material.Icons;
using CommunityToolkit.Mvvm.Input;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 可以放进主导航（SukiSideMenu）的页面。
/// 页面视图由 <see cref="Base.ViewLocator"/> 按 "xxxViewModel" → "xxxView" 的命名约定解析。
/// </summary>
public abstract partial class PageViewModel : BaseViewModel
{
    /// <summary>导航栏中显示的名称。</summary>
    public abstract string DisplayName { get; }

    /// <summary>导航栏中显示的图标。</summary>
    public abstract MaterialIconKind Icon { get; }

    /// <summary>
    /// 当前页面是否有「返回上一层」的动作（例如进入了游戏板块）。
    /// 为 true 时，标题栏左侧会显示返回按钮。
    /// </summary>
    public virtual bool CanGoBack => false;

    /// <summary>返回上一层。标题栏左侧的返回按钮会调用它。</summary>
    [RelayCommand]
    private void GoBack() => OnGoBack();

    protected virtual void OnGoBack()
    {
    }
}
