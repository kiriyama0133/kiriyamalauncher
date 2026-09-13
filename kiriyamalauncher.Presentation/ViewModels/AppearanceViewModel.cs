using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI;
using SukiUI.Models;
using System.Collections.Generic;
using System.Linq;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>明暗模式偏好。</summary>
public enum AppAppearance
{
    /// <summary>跟随系统。</summary>
    System,

    /// <summary>浅色。</summary>
    Light,

    /// <summary>深色。</summary>
    Dark
}

/// <summary>外观面板中的一个配色主题。</summary>
public partial class ColorThemeOption : ObservableObject
{
    private readonly AppearanceViewModel _owner;

    public SukiColorTheme Theme { get; }

    public string DisplayName => Theme.DisplayName;

    public IBrush PrimaryBrush => Theme.PrimaryBrush;

    /// <summary>是否是当前正在使用的配色。</summary>
    [ObservableProperty]
    private bool _isActive;

    public ColorThemeOption(AppearanceViewModel owner, SukiColorTheme theme)
    {
        _owner = owner;
        Theme = theme;
    }

    [RelayCommand]
    private void Apply() => _owner.ChangeColorTheme(this);
}

/// <summary>
/// 外观设置：明暗模式（跟随系统 / 浅色 / 深色）与配色主题，直接操作 SukiUI 的 <see cref="SukiTheme"/>。
/// </summary>
public partial class AppearanceViewModel : BaseViewModel
{
    private readonly SukiTheme _sukiTheme;

    /// <summary>可选的配色主题。</summary>
    public IReadOnlyList<ColorThemeOption> ColorThemes { get; }

    [ObservableProperty]
    private AppAppearance _appearance = AppAppearance.System;

    public bool? IsSystemTheme
    {
        get => Appearance == AppAppearance.System;
        set { if (value == true) Appearance = AppAppearance.System; }
    }

    public bool? IsLightTheme
    {
        get => Appearance == AppAppearance.Light;
        set { if (value == true) Appearance = AppAppearance.Light; }
    }

    public bool? IsDarkTheme
    {
        get => Appearance == AppAppearance.Dark;
        set { if (value == true) Appearance = AppAppearance.Dark; }
    }

    public AppearanceViewModel()
    {
        _sukiTheme = SukiTheme.GetInstance();

        ColorThemes = _sukiTheme.ColorThemes
            .Select(theme => new ColorThemeOption(this, theme))
            .ToList();

        SyncActiveColorTheme();
    }

    partial void OnAppearanceChanged(AppAppearance value)
    {
        _sukiTheme.ChangeBaseTheme(value switch
        {
            AppAppearance.Light => ThemeVariant.Light,
            AppAppearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        });

        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    internal void ChangeColorTheme(ColorThemeOption option)
    {
        _sukiTheme.ChangeColorTheme(option.Theme);
        SyncActiveColorTheme();
    }

    private void SyncActiveColorTheme()
    {
        foreach (ColorThemeOption option in ColorThemes)
        {
            option.IsActive = ReferenceEquals(option.Theme, _sukiTheme.ActiveColorTheme);
        }
    }
}
