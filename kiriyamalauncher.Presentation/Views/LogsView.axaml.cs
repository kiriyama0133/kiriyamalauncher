using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Presentation.Base.Extensions;
using kiriyamalauncher.Presentation.ViewModels;

namespace kiriyamalauncher.Presentation.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        this.SetDataContext(Ioc.Default);
        this.PlayPageEnterAnimation();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel viewModel)
        {
            await viewModel.LoadAsync();
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel viewModel)
        {
            viewModel.Detach();
        }
    }
}
