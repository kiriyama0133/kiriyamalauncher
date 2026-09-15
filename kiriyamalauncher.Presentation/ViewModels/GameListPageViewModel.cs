using CommunityToolkit.Mvvm.ComponentModel;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 「游戏列表」页面：放进 <see cref="SukiUI.Controls.SukiTransitioningContentControl"/> 作为第一个页面。
/// 只是 <see cref="GameSessionViewModel"/> 的一个轻量入口，
/// 模板通过 <see cref="Session"/> 路径绑定拿数据（这样源状态变化能自动通知）。
/// </summary>
public sealed partial class GameListPageViewModel : ObservableObject
{
    /// <summary>协调器：列表模板从这里拿 Games / ListStatusText。</summary>
    public GameSessionViewModel Session { get; }

    public GameListPageViewModel(GameSessionViewModel session)
    {
        Session = session;
    }
}
