using Avalonia.Controls;

namespace kiriyamalauncher.Presentation.Base.Controls;

/// <summary>
/// 游戏联机面板控件（DataContext 由使用方绑定为 <c>GameIntegrationViewModel</c>）。
/// </summary>
public partial class GameIntegrationControl : UserControl
{
    public GameIntegrationControl()
    {
        InitializeComponent();
    }
}
