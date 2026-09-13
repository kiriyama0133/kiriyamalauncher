using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Presentation.Base.Extensions;
using kiriyamalauncher.Presentation.ViewModels;
using System;

namespace kiriyamalauncher.Presentation.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        this.SetDataContext(Ioc.Default);
        this.PlayPageEnterAnimation();
    }

    /// <summary>ZeroTier 网络 ID 输入框的焦点联动：聚焦时显示「确认 / 取消」。</summary>
    private void OnNetworkIdFocusChanged(object? sender, RoutedEventArgs e)
        => UpdateEditingFlag("NetworkIdRow", static (viewModel, isInside) => viewModel.IsEditingNetworkId = isInside);

    /// <summary>Moon 服务器输入框的焦点联动：聚焦时显示「确认 / 取消」。</summary>
    private void OnMoonServerFocusChanged(object? sender, RoutedEventArgs e)
        => UpdateEditingFlag("MoonServerRow", static (viewModel, isInside) => viewModel.IsEditingMoonServer = isInside);

    /// <summary>
    /// 点确认/取消时焦点会先离开输入框，所以延迟一拍再判断焦点是否仍在这一行内。
    /// </summary>
    private void UpdateEditingFlag(string rowName, Action<SettingsViewModel, bool> apply)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not SettingsViewModel viewModel)
            {
                return;
            }

            IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            StackPanel? row = this.FindControl<StackPanel>(rowName);

            bool isInsideRow = focused is Visual visual
                && row is not null
                && (ReferenceEquals(row, visual) || row.IsVisualAncestorOf(visual));

            apply(viewModel, isInsideRow);
        }, DispatcherPriority.Input);
    }
}
