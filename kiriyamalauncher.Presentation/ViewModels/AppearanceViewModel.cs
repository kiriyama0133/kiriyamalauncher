using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
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
    private readonly IAppSnapshot _snapshot;

    /// <summary>正在从偏好同步到界面时，避免再次触发回写。</summary>
    private bool _isSyncing;

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

    public AppearanceViewModel(IAppSnapshot snapshot)
    {
        _snapshot = snapshot;
        _sukiTheme = SukiTheme.GetInstance();

        ColorThemes = _sukiTheme.ColorThemes
            .Select(theme => new ColorThemeOption(this, theme))
            .ToList();

        SyncFromPreferences();
        SyncActiveColorTheme();

        snapshot.Changed += OnSnapshotChanged;
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

        if (_isSyncing)
        {
            return;
        }

        // 明暗模式与配色一样，都要落库（这里显式回写，不依赖 SukiUI 的事件反查）。
        _snapshot.UpdateBaseTheme(value switch
        {
            AppAppearance.Light => "Light",
            AppAppearance.Dark => "Dark",
            _ => "Default"
        });
    }

    internal void ChangeColorTheme(ColorThemeOption option)
    {
        _sukiTheme.ChangeColorTheme(option.Theme);
        SyncActiveColorTheme();

        if (_isSyncing)
        {
            return;
        }

        // 用户点选的就是这个主题，直接按名字存下来（显示名就是内置配色的名字，如 Blue）。
        _snapshot.UpdateColorTheme(option.Theme.DisplayName);
    }

    private void OnSnapshotChanged(object? sender, System.EventArgs e)
    {
        if (_isSyncing)
        {
            return;
        }

        SyncFromPreferences();
        SyncActiveColorTheme();
    }

    /// <summary>把偏好里的明暗模式同步到界面选择上（不触发回写）。</summary>
    private void SyncFromPreferences()
    {
        _isSyncing = true;
        Appearance = _snapshot.Preferences.BaseTheme switch
        {
            "Light" => AppAppearance.Light,
            "Dark" => AppAppearance.Dark,
            _ => AppAppearance.System
        };
        _isSyncing = false;
    }

    private void SyncActiveColorTheme()
    {
        foreach (ColorThemeOption option in ColorThemes)
        {
            option.IsActive = ReferenceEquals(option.Theme, _sukiTheme.ActiveColorTheme);
        }
    }
}
