using CommunityToolkit.Mvvm.ComponentModel;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 「游戏板块」页面：放进 <see cref="SukiUI.Controls.SukiTransitioningContentControl"/> 作为第二层页面。
/// 是 <see cref="GameSessionViewModel"/> 的轻量入口，
/// 模板通过 <see cref="Session"/> 路径绑定拿数据与命令（这样源状态变化能自动通知）。
/// </summary>
public sealed partial class GameBoardPageViewModel : ObservableObject
{
    public GameViewModel Game { get; }

    /// <summary>协调器：板块模板从这里拿 StatusText / IsConnected / Integration / LanDevices 等。</summary>
    public GameSessionViewModel Session { get; }

    public GameBoardPageViewModel(GameSessionViewModel session, GameViewModel game)
    {
        Session = session;
        Game = game;
    }
}
