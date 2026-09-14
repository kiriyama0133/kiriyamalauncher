using Avalonia.Controls;

namespace kiriyamalauncher.Presentation.Base.Controls;

/// <summary>
/// 局域网设备卡片（DataContext 由使用方绑定为 <c>LanDevicesViewModel</c>）。
/// </summary>
public partial class LanDevicesControl : UserControl
{
    public LanDevicesControl()
    {
        InitializeComponent();
    }
}
