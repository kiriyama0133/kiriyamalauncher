using Avalonia.Controls;
using kiriyamalauncher.Presentation.Base.Extensions;
using CommunityToolkit.Mvvm.DependencyInjection;

namespace kiriyamalauncher.Presentation.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        this.SetDataContext(Ioc.Default);
    }
}
