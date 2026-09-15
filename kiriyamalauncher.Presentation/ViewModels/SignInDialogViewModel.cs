using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI.Dialogs;
using System;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 账号对话框：登录 / 注册两种模式，沿用 demo Dashboard 的 BusyArea 表单设计。
/// 真实鉴权走中继服务器的 PKCE + OAuth 流程；密码一律不落本地库。
/// </summary>
public partial class SignInDialogViewModel : BaseViewModel
{
    private readonly ISukiDialog _dialog;
    private readonly IAppSnapshot _snapshot;
    private readonly IAppNotifier _notifier;

    [ObservableProperty]
    private string _loginName = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    /// <summary>表单是否处于忙碌状态（BusyArea 用它盖住整块表单）。</summary>
    [ObservableProperty]
    private bool _isSigningIn;

    /// <summary>是否是注册模式。</summary>
    [ObservableProperty]
    private bool _isRegisterMode;

    public string TitleText => IsRegisterMode ? "注册账号" : "登录账号";

    /// <summary>第一个输入框标签：登录时填邮箱，注册时填名称。</summary>
    public string IdentifierLabel => IsRegisterMode ? "名称" : "邮箱";

    /// <summary>第一个输入框水印：登录时是邮箱格式提示，注册时是名称提示。</summary>
    public string IdentifierWatermark => IsRegisterMode ? "例如 kiriyama" : "name@example.com";

    public string SubmitText => IsRegisterMode ? "注册" : "登录";

    public string SwitchText => IsRegisterMode ? "已有账号？去登录" : "没有账号？去注册";

    /// <summary>字段级错误提示（null 表示没有错误）。</summary>
    [ObservableProperty]
    private string? _loginNameError;

    [ObservableProperty]
    private string? _emailError;

    [ObservableProperty]
    private string? _passwordError;

    [ObservableProperty]
    private string? _confirmPasswordError;

    public SignInDialogViewModel(ISukiDialog dialog, IAppSnapshot snapshot, IAppNotifier notifier)
    {
        _dialog = dialog;
        _snapshot = snapshot;
        _notifier = notifier;
    }

    partial void OnIsRegisterModeChanged(bool value)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(IdentifierLabel));
        OnPropertyChanged(nameof(IdentifierWatermark));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(SwitchText));
        ClearErrors();
    }

    partial void OnLoginNameChanged(string value) => LoginNameError = null;
    partial void OnEmailChanged(string value) => EmailError = null;
    partial void OnPasswordChanged(string value) { PasswordError = null; ConfirmPasswordError = null; }
    partial void OnConfirmPasswordChanged(string value) => ConfirmPasswordError = null;

    /// <summary>在登录 / 注册之间切换。</summary>
    [RelayCommand]
    private void SwitchMode() => IsRegisterMode = !IsRegisterMode;

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsSigningIn)
        {
            return;
        }

        string loginName = LoginName.Trim();
        string email = Email.Trim();
        string password = Password;

        ClearErrors();

        bool hasError = false;

        if (IsRegisterMode)
        {
            // 注册：名称（displayName）+ 邮箱 + 密码 + 确认密码。
            if (string.IsNullOrWhiteSpace(loginName))
            {
                LoginNameError = "请填写名称";
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                EmailError = "请填写邮箱";
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                PasswordError = "请填写密码";
                hasError = true;
            }
            else if (password.Length < 8)
            {
                PasswordError = "密码至少 8 位";
                hasError = true;
            }

            if (!string.IsNullOrEmpty(password) && !string.Equals(password, ConfirmPassword, System.StringComparison.Ordinal))
            {
                ConfirmPasswordError = "两次输入的密码不一致";
                hasError = true;
            }
        }
        else
        {
            // 登录：邮箱 + 密码。
            if (string.IsNullOrWhiteSpace(loginName))
            {
                LoginNameError = "请填写邮箱";
                hasError = true;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                PasswordError = "请填写密码";
                hasError = true;
            }
        }

        if (hasError)
        {
            return;
        }

        IsSigningIn = true;

        try
        {
            if (IsRegisterMode)
            {
                await _snapshot.RegisterAsync(email, password, loginName);
                _notifier.Success("注册成功", $"账号「{email}」已创建并登录。");
            }
            else
            {
                await _snapshot.SignInAsync(loginName, password);
                _notifier.Success("登录成功", $"欢迎回来，{_snapshot.User.Nickname}。");
            }

            _dialog.Dismiss();
        }
        catch (RelayServerException ex)
        {
            _notifier.Error(IsRegisterMode ? "注册失败" : "登录失败", ex.Message);
        }
        catch (Exception ex)
        {
            // 网络超时 / 连接失败等非业务异常也要给出可读反馈，不能让它冒泡导致界面「卡死」。
            _notifier.Error(IsRegisterMode ? "注册失败" : "登录失败",
                $"无法连接中继服务器：{ex.Message}");
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    private void ClearErrors()
    {
        LoginNameError = null;
        EmailError = null;
        PasswordError = null;
        ConfirmPasswordError = null;
    }
}
