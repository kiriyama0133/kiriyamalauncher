using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Presentation.Base.Extensions;

namespace kiriyamalauncher.Presentation.Views;

public partial class GameSessionView : UserControl
{
    public GameSessionView()
    {
        InitializeComponent();
        this.SetDataContext(Ioc.Default);
        this.PlayPageEnterAnimation();
    }
}
