using Material.Icons;
using CommunityToolkit.Mvvm.Input;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System.Threading.Tasks;

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
    private Task GoBackAsync() => OnGoBackAsync();

    /// <summary>
    /// 返回动作的实现。子类可以覆盖它（例如返回前先弹一个确认框）。
    /// </summary>
    protected virtual Task OnGoBackAsync() => Task.CompletedTask;
}
