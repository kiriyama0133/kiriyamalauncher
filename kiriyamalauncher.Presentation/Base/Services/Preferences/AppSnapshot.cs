using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;
using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using Microsoft.Extensions.Logging;
using SukiUI;
using SukiUI.Enums;
using SukiUI.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Preferences;

/// <summary>
/// 全局快照读写器的默认实现。
///
/// - 账号与偏好分别来自两张表，互不影响；
/// - 离散改动（主题色、明暗、ZeroTier、网络 ID）立即写库；
/// - 字号会随滑块连续变化，所以单独做防抖；
/// - 退出前调用 <see cref="FlushAsync"/> 保证最后的状态一定落库。
/// </summary>
public class AppSnapshot : IAppSnapshot
{
    /// <summary>字号写库防抖时间。</summary>
    private static readonly TimeSpan SAVE_DEBOUNCE = TimeSpan.FromMilliseconds(600);

    private const double MIN_FONT_SIZE = 12d;
    private const double MAX_FONT_SIZE = 22d;

    /// <summary>行高与字号的比值：把「一行文字」换算成固定像素高度时用。</summary>
    private const double LINE_HEIGHT_RATIO = 1.4d;

    // 下面几个是游戏卡片（GameCard.axaml）里写死的排版尺寸，换算卡片高度时要用到；
    // 改动卡片的封面高度 / 内边距时，这里要同步改。
    private const double GAME_CARD_COVER_HEIGHT = 120d;
    private const double GAME_CARD_TEXT_TOP_PADDING = 12d;
    private const double GAME_CARD_TEXT_BOTTOM_PADDING = 14d;
    private const double GAME_CARD_TEXT_SPACING = 6d;
    private const double GAME_CARD_BORDER_THICKNESS = 2d;
    private const double GAME_CARD_DESC_SAFETY = 2d;

    private const string DEFAULT_COLOR_THEME = "Blue";
    private const string DEFAULT_BASE_THEME = "Default";

    /// <summary>OAuth 客户端标识（与服务端约定的桌面启动器 client_id）。</summary>
    private const string CLIENT_ID = "kiriyamalauncher";

    private readonly IUserService _userService;
    private readonly IUserPreferencesService _preferencesService;
    private readonly IRelayServerClient _relay;
    private readonly ILogger<AppSnapshot> _logger;
    private readonly SukiTheme _sukiTheme;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private CancellationTokenSource? _fontSizeDebounce;
    private bool _userDirty;
    private bool _preferencesDirty;

    public AppSnapshot(
        IUserService userService,
        IUserPreferencesService preferencesService,
        IRelayServerClient relay,
        ILogger<AppSnapshot> logger)
    {
        _userService = userService;
        _preferencesService = preferencesService;
        _relay = relay;
        _logger = logger;
        _sukiTheme = SukiTheme.GetInstance();
    }

    public UserDto User { get; private set; } = new();

    public UserPreferencesDto Preferences { get; private set; } = new();

    public bool IsLoaded { get; private set; }

    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        try
        {
            User = await _userService.GetOrCreateCurrentAsync();
            Preferences = await _preferencesService.GetOrCreateAsync();

            NormalizePreferences();
            ApplyFontSize();
            ApplyTheme();

            IsLoaded = true;
            RaiseChanged();

            _logger.LogInformation(
                "已恢复账号与偏好：昵称「{Nickname}」，字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}，网络 {NetworkId}。",
                User.Nickname, Preferences.FontSize, Preferences.BaseTheme, Preferences.ColorTheme, Preferences.ZeroTierNetworkId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复账号与偏好失败，使用默认值。");
        }
    }

    public void UpdateFontSize(double fontSize)
    {
        double clamped = Math.Clamp(Math.Round(fontSize), MIN_FONT_SIZE, MAX_FONT_SIZE);
        if (Math.Abs(Preferences.FontSize - clamped) < 0.01d)
        {
            return;
        }

        Preferences.FontSize = clamped;
        ApplyFontSize();
        RaiseChanged();
        _preferencesDirty = true;
        ScheduleFontSizeSave();
    }

    public void UpdateMoonServerIp(string moonServerIp)
    {
        string value = moonServerIp.Trim();
        if (string.Equals(Preferences.MoonServerIp, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.MoonServerIp = value;
        SavePreferencesImmediately("Moon 服务器");
    }

    public void UpdateZeroTierNetworkId(string networkId)
    {
        string value = networkId.Trim().ToLowerInvariant();
        if (string.Equals(Preferences.ZeroTierNetworkId, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.ZeroTierNetworkId = value;
        SavePreferencesImmediately("ZeroTier 网络 ID");
    }

    public void UpdateSelfHostedNetworkId(string networkId)
    {
        string value = networkId.Trim().ToLowerInvariant();
        if (string.Equals(Preferences.SelfHostedNetworkId, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.SelfHostedNetworkId = value;
        SavePreferencesImmediately("自建控制器网络 ID");
    }

    public void UpdateZeroTierConnectionMode(string connectionMode)
    {
        string value = connectionMode switch
        {
            _ when string.Equals(connectionMode, ZeroTierSettings.SelfHostedController, StringComparison.OrdinalIgnoreCase)
                => ZeroTierSettings.SelfHostedController,
            _ when string.Equals(connectionMode, ZeroTierSettings.RelayServer, StringComparison.OrdinalIgnoreCase)
                => ZeroTierSettings.RelayServer,
            _ => ZeroTierSettings.OfficialController,
        };

        if (string.Equals(Preferences.ZeroTierConnectionMode, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.ZeroTierConnectionMode = value;
        SavePreferencesImmediately("ZeroTier 连接模式");
    }

    public void UpdateRelayServerIp(string relayServerIp)
    {
        string value = relayServerIp.Trim();
        if (string.Equals(Preferences.RelayServerIp, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.RelayServerIp = value;
        SavePreferencesImmediately("中继服务器 IP");
    }

    public void UpdateRelayServerPort(int relayServerPort)
    {
        int value = relayServerPort is > 0 and <= 65535 ? relayServerPort : ZeroTierSettings.DefaultRelayServerPort;
        if (Preferences.RelayServerPort == value)
        {
            return;
        }

        Preferences.RelayServerPort = value;
        SavePreferencesImmediately("中继服务器端口");
    }

    public void UpdateZeroTierTransportBackend(string transportBackend)
    {
        string value = string.Equals(transportBackend, ZeroTierSettings.ClientBackend, StringComparison.OrdinalIgnoreCase)
            ? ZeroTierSettings.ClientBackend
            : ZeroTierSettings.SocketsBackend;

        if (string.Equals(Preferences.ZeroTierTransportBackend, value, StringComparison.Ordinal))
        {
            return;
        }

        Preferences.ZeroTierTransportBackend = value;
        SavePreferencesImmediately("ZeroTier 传输引擎");
    }

    public void UpdateBaseTheme(string baseTheme)
    {
        string value = string.IsNullOrWhiteSpace(baseTheme) ? DEFAULT_BASE_THEME : baseTheme.Trim();
        if (string.Equals(Preferences.BaseTheme, value, StringComparison.Ordinal))
        {
            return;
        }

        // 主题本身已经由 AppearanceViewModel 切换过了，这里只更新快照并落库。
        Preferences.BaseTheme = value;
        SavePreferencesImmediately("明暗模式");
    }

    public void UpdateColorTheme(string colorTheme)
    {
        string value = string.IsNullOrWhiteSpace(colorTheme) ? DEFAULT_COLOR_THEME : colorTheme.Trim();
        if (string.Equals(Preferences.ColorTheme, value, StringComparison.Ordinal))
        {
            return;
        }

        // 配色同样由 AppearanceViewModel 切换，这里只负责记录。
        Preferences.ColorTheme = value;
        SavePreferencesImmediately("配色主题");
    }

    public async Task SignInAsync(string email, string password)
    {
        string baseUrl = ResolveRelayBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new RelayServerException("请先在设置里填写中继服务器地址。");
        }

        try
        {
            // PKCE：先随机生成 code_verifier，再算它的 S256 code_challenge 交给登录端点。
            string codeVerifier = RelayPkce.GenerateCodeVerifier();
            string codeChallenge = RelayPkce.ComputeCodeChallenge(codeVerifier);

            RelayAuthorizationCode authorizationCode = await _relay.LoginAsync(
                baseUrl, email.Trim(), password, codeChallenge);

            RelayTokenSet tokens = await _relay.ExchangeCodeAsync(
                baseUrl, authorizationCode.Code, codeVerifier, CLIENT_ID);

            ApplySignedIn(email.Trim(), authorizationCode.DisplayName, tokens);
        }
        catch (RelayServerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RelayServerException($"无法连接中继服务器：{ex.Message}", ex);
        }
    }

    public async Task RegisterAsync(string email, string password, string displayName)
    {
        string baseUrl = ResolveRelayBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new RelayServerException("请先在设置里填写中继服务器地址。");
        }

        string normalizedEmail = email.Trim();
        string normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim();

        try
        {
            await _relay.RegisterAsync(baseUrl, normalizedEmail, password, normalizedDisplayName);

            // 注册成功后自动登录：直接走 PKCE 换令牌，让用户无需再手动登录一次。
            string codeVerifier = RelayPkce.GenerateCodeVerifier();
            string codeChallenge = RelayPkce.ComputeCodeChallenge(codeVerifier);

            RelayAuthorizationCode authorizationCode = await _relay.LoginAsync(
                baseUrl, normalizedEmail, password, codeChallenge);

            RelayTokenSet tokens = await _relay.ExchangeCodeAsync(
                baseUrl, authorizationCode.Code, codeVerifier, CLIENT_ID);

            ApplySignedIn(normalizedEmail, normalizedDisplayName, tokens);
        }
        catch (RelayServerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RelayServerException($"无法连接中继服务器：{ex.Message}", ex);
        }
    }

    /// <summary>登录 / 注册成功后，把账号信息与令牌写进快照并落库。</summary>
    private void ApplySignedIn(string email, string displayName, RelayTokenSet tokens)
    {
        // 昵称用服务端返回的显示名（名称），绝不回退到邮箱 —— 界面顶端与房主卡片都只显示名称。
        User.Nickname = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim();
        User.LoginName = email;
        User.Email = email;
        User.AccessToken = tokens.AccessToken;
        User.RefreshToken = tokens.RefreshToken;
        User.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
        User.ProfileUpdatedAt = DateTime.Now;

        _userDirty = true;
        RaiseChanged();

        _ = Task.Run(SaveUserAsync);
    }

    /// <summary>从偏好拼出中继服务器 baseUrl；没配置时返回空串。</summary>
    private string ResolveRelayBaseUrl()
    {
        string ip = (Preferences.RelayServerIp ?? string.Empty).Trim();
        int port = Preferences.RelayServerPort;

        if (string.IsNullOrWhiteSpace(ip))
        {
            return string.Empty;
        }

        if (ip.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || ip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ip.TrimEnd('/');
        }

        return $"http://{ip}:{port}";
    }

    public async Task SignOutAsync()
    {
        // 只清账号信息；偏好与账号无关，保持不动。
        User.LoginName = string.Empty;
        User.Nickname = string.Empty;
        User.Email = string.Empty;
        User.AccessToken = string.Empty;
        User.RefreshToken = string.Empty;
        User.AccessTokenExpiresAt = null;
        User.ProfileUpdatedAt = DateTime.Now;

        await SaveUserAsync();
        RaiseChanged();
    }

    public Task SaveAsync() => FlushAsync();

    /// <summary>退出前调用：取消防抖并等待所有未落库的改动写完。</summary>
    public async Task FlushAsync()
    {
        _fontSizeDebounce?.Cancel();
        _fontSizeDebounce?.Dispose();
        _fontSizeDebounce = null;

        if (_preferencesDirty)
        {
            await SavePreferencesAsync();
        }

        if (_userDirty)
        {
            await SaveUserAsync();
        }
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

    /// <summary>补齐空值：老库里没有网络 ID / 连接模式时用默认值。</summary>
    private void NormalizePreferences()
    {
        if (!ZeroTierSettings.IsValidNetworkId(Preferences.ZeroTierNetworkId))
        {
            Preferences.ZeroTierNetworkId = ZeroTierSettings.DefaultNetworkId;
        }

        // 自建控制器网络 ID 没有合理的默认值（由用户自己的控制器生成），无效时清空，
        // 让连接流程在自建模式下提示用户填写。
        if (!ZeroTierSettings.IsValidNetworkId(Preferences.SelfHostedNetworkId))
        {
            Preferences.SelfHostedNetworkId = string.Empty;
        }

        Preferences.ZeroTierConnectionMode = string.Equals(
            Preferences.ZeroTierConnectionMode,
            ZeroTierSettings.SelfHostedController,
            StringComparison.OrdinalIgnoreCase)
            ? ZeroTierSettings.SelfHostedController
            : string.Equals(
                Preferences.ZeroTierConnectionMode,
                ZeroTierSettings.RelayServer,
                StringComparison.OrdinalIgnoreCase)
                ? ZeroTierSettings.RelayServer
                : ZeroTierSettings.OfficialController;

        if (Preferences.RelayServerPort is < 1 or > 65535)
        {
            Preferences.RelayServerPort = ZeroTierSettings.DefaultRelayServerPort;
        }

        Preferences.ZeroTierTransportBackend = string.Equals(
            Preferences.ZeroTierTransportBackend,
            ZeroTierSettings.ClientBackend,
            StringComparison.OrdinalIgnoreCase)
            ? ZeroTierSettings.ClientBackend
            : ZeroTierSettings.SocketsBackend;

        Preferences.FontSize = Math.Clamp(Math.Round(Preferences.FontSize), MIN_FONT_SIZE, MAX_FONT_SIZE);

        if (string.IsNullOrWhiteSpace(Preferences.BaseTheme))
        {
            Preferences.BaseTheme = DEFAULT_BASE_THEME;
        }

        if (string.IsNullOrWhiteSpace(Preferences.ColorTheme))
        {
            Preferences.ColorTheme = DEFAULT_COLOR_THEME;
        }
    }

    /// <summary>
    /// 套用字号：同时更新我们自己的界面资源、SukiUI/Avalonia 的字号资源以及窗口字号，
    /// 视图里用 DynamicResource 引用这些键，所以改字号会立刻作用到整个界面。
    /// </summary>
    private void ApplyFontSize()
    {
        if (Application.Current is null)
        {
            return;
        }

        double fontSize = Preferences.FontSize;

        // 我们自己的界面资源。
        Application.Current.Resources["AppFontSizeSmall"] = Math.Max(MIN_FONT_SIZE - 2d, fontSize - 2d);
        Application.Current.Resources["AppFontSizeCompact"] = Math.Max(MIN_FONT_SIZE - 1d, fontSize - 1d);
        Application.Current.Resources["AppFontSizeNormal"] = fontSize;
        Application.Current.Resources["AppFontSizeLarge"] = fontSize + 1d;
        Application.Current.Resources["AppFontSizeSubtitle"] = fontSize + 4d;
        Application.Current.Resources["AppFontSizeTitle"] = fontSize + 12d;

        // 游戏卡片尺寸：宽固定 220（写在 GameCard.axaml），高由这里算好后写进资源。
        // 卡片高度不随内容撑开，简介固定占两行，所以同排卡片永远等高、底边对齐。
        double descLineHeight = Math.Round((fontSize - 2d) * LINE_HEIGHT_RATIO);
        double descHeight = (descLineHeight * 2d) + GAME_CARD_DESC_SAFETY;
        double titleLineHeight = Math.Round((fontSize + 1d) * LINE_HEIGHT_RATIO);

        Application.Current.Resources["AppGameCardDescHeight"] = descHeight;
        Application.Current.Resources["AppGameCardHeight"] = GAME_CARD_COVER_HEIGHT
            + GAME_CARD_TEXT_TOP_PADDING + titleLineHeight + GAME_CARD_TEXT_SPACING
            + descHeight + GAME_CARD_TEXT_BOTTOM_PADDING + GAME_CARD_BORDER_THICKNESS;

        // SukiUI / Avalonia 自带控件用的字号资源。
        Application.Current.Resources["FontSizeSmall"] = Math.Max(MIN_FONT_SIZE - 2d, fontSize - 1d);
        Application.Current.Resources["FontSizeNormal"] = fontSize;
        Application.Current.Resources["FontSizeLarge"] = fontSize + 1d;

        // 窗口字号：控件的默认字号（没有显式设置 FontSize 的地方）都继承它。
        if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.FontSize = fontSize;
        }
        else
        {
            _logger.LogInformation("主窗口尚未创建，字号将在窗口创建后由界面资源生效。");
        }
    }

    private void ApplyTheme()
    {
        _sukiTheme.ChangeBaseTheme(Preferences.BaseTheme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        });

        ApplyColorTheme(Preferences.ColorTheme);
    }

    /// <summary>按名字切配色：先按显示名匹配已有的配色，再退回按 SukiColor 解析。</summary>
    private void ApplyColorTheme(string colorTheme)
    {
        SukiColorTheme? theme = _sukiTheme.ColorThemes
            .FirstOrDefault(candidate => string.Equals(candidate.DisplayName, colorTheme, StringComparison.OrdinalIgnoreCase));

        if (theme is not null)
        {
            _sukiTheme.ChangeColorTheme(theme);
            return;
        }

        if (Enum.TryParse(colorTheme, ignoreCase: true, out SukiColor color))
        {
            _sukiTheme.ChangeColorTheme(color);
            return;
        }

        _logger.LogWarning("未知的配色主题「{ColorTheme}」，保持当前配色。", colorTheme);
    }

    /// <summary>记录改动并立即写库（不阻塞界面）。</summary>
    private void SavePreferencesImmediately(string reason)
    {
        _preferencesDirty = true;
        RaiseChanged();

        _logger.LogInformation("偏好已更新（{Reason}），正在写库。", reason);

        _ = Task.Run(SavePreferencesAsync);
    }

    /// <summary>字号防抖写库：连续变化只在停止 600ms 后写一次。</summary>
    private void ScheduleFontSizeSave()
    {
        _fontSizeDebounce?.Cancel();
        _fontSizeDebounce?.Dispose();

        CancellationTokenSource source = new();
        _fontSizeDebounce = source;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SAVE_DEBOUNCE, source.Token);
                await SavePreferencesAsync();
            }
            catch (OperationCanceledException)
            {
                // 被新的改动取代，忽略。
            }
        });
    }

    private async Task SavePreferencesAsync()
    {
        await _saveLock.WaitAsync();

        try
        {
            await _preferencesService.SaveAsync(Preferences);
            _preferencesDirty = false;

            _logger.LogInformation(
                "偏好已写入 SQLite：字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}。",
                Preferences.FontSize, Preferences.BaseTheme, Preferences.ColorTheme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入偏好失败。");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task SaveUserAsync()
    {
        _userDirty = true;

        await _saveLock.WaitAsync();

        try
        {
            await _userService.SaveAsync(User);
            _userDirty = false;

            _logger.LogInformation("账号已写入 SQLite：昵称「{Nickname}」。", User.Nickname);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入账号失败。");
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
