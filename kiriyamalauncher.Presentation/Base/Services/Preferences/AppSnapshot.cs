using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;
using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using kiriyamalauncher.Data.Entities;
using Microsoft.Extensions.Logging;
using SukiUI;
using SukiUI.Enums;
using SukiUI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Preferences;

/// <summary>
/// 全局快照读写器的默认实现。
///
/// - <see cref="LoadAsync"/>：从 SQLite 取出用户资料与偏好，套用到界面（字号 + 主题）。
/// - <see cref="UpdateFontSize"/>：先改界面（立即生效），再防抖写库；拖动滑块时不会疯狂写数据库。
/// - 主题在标题栏外观面板里切换时，通过 SukiUI 的事件回写快照。
/// </summary>
public class AppSnapshot : IAppSnapshot
{
    /// <summary>写库防抖时间。</summary>
    private static readonly TimeSpan SAVE_DEBOUNCE = TimeSpan.FromMilliseconds(600);

    private const double MIN_FONT_SIZE = 12d;
    private const double MAX_FONT_SIZE = 22d;

    private readonly IUserProfileService _userProfileService;
    private readonly ILogger<AppSnapshot> _logger;
    private readonly SukiTheme _sukiTheme;

    private CancellationTokenSource? _debounceTokenSource;

    public AppSnapshot(IUserProfileService userProfileService, ILogger<AppSnapshot> logger)
    {
        _userProfileService = userProfileService;
        _logger = logger;

        _sukiTheme = SukiTheme.GetInstance();
        _sukiTheme.OnBaseThemeChanged += OnBaseThemeChanged;
        _sukiTheme.OnColorThemeChanged += OnColorThemeChanged;
    }

    public UserProfileDto Profile { get; private set; } = new();

    public bool IsLoaded { get; private set; }

    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        try
        {
            Profile = await _userProfileService.GetOrCreateAsync();
            NormalizeProfile();
            ApplyProfile();
            IsLoaded = true;
            RaiseChanged();
            _logger.LogInformation("已恢复用户偏好：字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}。", Profile.FontSize, Profile.BaseTheme, Profile.ColorTheme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复用户偏好失败，使用默认值。");
        }
    }

    public void UpdateFontSize(double fontSize)
    {
        double clamped = Math.Clamp(Math.Round(fontSize), MIN_FONT_SIZE, MAX_FONT_SIZE);
        if (Math.Abs(Profile.FontSize - clamped) < 0.01d)
        {
            return;
        }

        Profile.FontSize = clamped;
        ApplyFontSize();
        RaiseChanged();
        ScheduleSave();
    }

    public async Task<bool> SignInAsync(string loginNameOrEmail, string password)
    {
        string identifier = loginNameOrEmail.Trim();

        // 没有匹配的账号时按输入的名称登录（测试阶段不做真实鉴权，password 不参与）。
        bool matched = string.Equals(Profile.LoginName, identifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Profile.Email, identifier, StringComparison.OrdinalIgnoreCase);

        if (!matched)
        {
            Profile.LoginName = identifier;
            Profile.Nickname = identifier;
        }

        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        await SaveProfileAsync();

        return matched;
    }

    public async Task RegisterAsync(string loginName, string nickname, string email, string password)
    {
        // 测试阶段：password 只用于界面校验，不写入数据库。
        Profile.LoginName = loginName.Trim();
        Profile.Nickname = string.IsNullOrWhiteSpace(nickname) ? Profile.LoginName : nickname.Trim();
        Profile.Email = email.Trim();
        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        await SaveProfileAsync();
    }

    public async Task SignOutAsync()
    {
        Profile.LoginName = string.Empty;
        Profile.Nickname = string.Empty;
        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        await SaveProfileAsync();
    }

    public Task SaveAsync() => SaveProfileAsync();

    public void UpdateMoonServerIp(string moonServerIp)
    {
        string value = moonServerIp.Trim();
        if (string.Equals(Profile.MoonServerIp, value, StringComparison.Ordinal))
        {
            return;
        }

        Profile.MoonServerIp = value;
        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        ScheduleSave();
    }

    public void UpdateZeroTierNetworkId(string networkId)
    {
        string value = networkId.Trim().ToLowerInvariant();
        if (string.Equals(Profile.ZeroTierNetworkId, value, StringComparison.Ordinal))
        {
            return;
        }

        Profile.ZeroTierNetworkId = value;
        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        ScheduleSave();
    }

    public void UpdateZeroTierConnectionMode(string connectionMode)
    {
        string value = string.Equals(connectionMode, ZeroTierSettings.SelfHostedController, StringComparison.OrdinalIgnoreCase)
            ? ZeroTierSettings.SelfHostedController
            : ZeroTierSettings.OfficialController;

        if (string.Equals(Profile.ZeroTierConnectionMode, value, StringComparison.Ordinal))
        {
            return;
        }

        Profile.ZeroTierConnectionMode = value;
        Profile.ProfileUpdatedAt = DateTime.Now;

        RaiseChanged();
        ScheduleSave();
    }

    /// <summary>
    /// 补齐老库里的空值：没有网络 ID 时用默认网络，连接模式只接受两个已知取值。
    /// （不额外写库，等用户的下一次改动一并落库。）
    /// </summary>
    private void NormalizeProfile()
    {
        if (!ZeroTierSettings.IsValidNetworkId(Profile.ZeroTierNetworkId))
        {
            Profile.ZeroTierNetworkId = ZeroTierSettings.DefaultNetworkId;
        }

        Profile.ZeroTierConnectionMode = string.Equals(
            Profile.ZeroTierConnectionMode,
            ZeroTierSettings.SelfHostedController,
            StringComparison.OrdinalIgnoreCase)
            ? ZeroTierSettings.SelfHostedController
            : ZeroTierSettings.OfficialController;
    }

    /// <summary>Changed 事件统一在 UI 线程上触发。</summary>
    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    private void ApplyProfile()
    {
        ApplyFontSize();
        ApplyTheme();
    }

    /// <summary>套用字号：同时更新 SukiUI 的字号资源与窗口自身字号（子控件继承）。</summary>
    private void ApplyFontSize()
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.Resources["FontSizeNormal"] = Profile.FontSize;
        Application.Current.Resources["FontSizeSmall"] = Math.Max(MIN_FONT_SIZE - 2d, Profile.FontSize - 1d);
        Application.Current.Resources["FontSizeLarge"] = Profile.FontSize + 1d;

        if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.FontSize = Profile.FontSize;
        }
    }

    private void ApplyTheme()
    {
        _sukiTheme.ChangeBaseTheme(Profile.BaseTheme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        });

        if (Enum.TryParse(Profile.ColorTheme, ignoreCase: true, out SukiColor color))
        {
            _sukiTheme.ChangeColorTheme(color);
        }
    }

    private void OnBaseThemeChanged(ThemeVariant variant)
    {
        Profile.BaseTheme = variant.ToString();
        ScheduleSave();
    }

    private void OnColorThemeChanged(SukiColorTheme theme)
    {
        // 注意：SukiUI 切换配色时不会回写 SukiTheme.ThemeColor（它只在初始化时赋值），
        // 所以这里用内置配色字典反查对应的 SukiColor。（自定义配色不在字典里，保持原值。）
        foreach ((SukiColor color, SukiColorTheme defaultTheme) in SukiTheme.DefaultColorThemes)
        {
            if (!ReferenceEquals(defaultTheme, theme))
            {
                continue;
            }

            if (!string.Equals(Profile.ColorTheme, color.ToString(), StringComparison.Ordinal))
            {
                Profile.ColorTheme = color.ToString();
                ScheduleSave();
            }

            return;
        }
    }

    /// <summary>防抖写库：连续变化只在停止 600ms 后写一次。</summary>
    private void ScheduleSave()
    {
        _debounceTokenSource?.Cancel();
        _debounceTokenSource?.Dispose();

        CancellationTokenSource source = new();
        _debounceTokenSource = source;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SAVE_DEBOUNCE, source.Token);
                await SaveProfileAsync();
            }
            catch (OperationCanceledException)
            {
                // 被新的改动取代，忽略。
            }
        });
    }

    private async Task SaveProfileAsync()
    {
        try
        {
            await _userProfileService.SaveAsync(Profile);
            _logger.LogInformation("偏好已写入 SQLite：字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}。", Profile.FontSize, Profile.BaseTheme, Profile.ColorTheme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入用户偏好失败。");
        }
    }
}
