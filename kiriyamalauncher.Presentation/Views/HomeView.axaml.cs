using Avalonia.Controls;
using kiriyamalauncher.Presentation.Base.Extensions;
using CommunityToolkit.Mvvm.DependencyInjection;

namespace kiriyamalauncher.Presentation.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        this.SetDataContext(Ioc.Default);
        this.PlayPageEnterAnimation();
    }
}
