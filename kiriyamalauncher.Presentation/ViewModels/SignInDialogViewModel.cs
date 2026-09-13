using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI.Dialogs;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 账号对话框：登录 / 注册两种模式，沿用 demo Dashboard 的 BusyArea 表单设计。
/// 真实鉴权还没接，这里都用 2 秒延时模拟；账号信息会写进测试数据库，密码一律不落库。
/// </summary>
public partial class SignInDialogViewModel : BaseViewModel
{
    private readonly ISukiDialog _dialog;
    private readonly IAppSnapshot _snapshot;
    private readonly IAppNotifier _notifier;

    [ObservableProperty]
    private string _loginName = string.Empty;

    [ObservableProperty]
    private string _nickname = string.Empty;

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

    public string IdentifierLabel => IsRegisterMode ? "名称" : "名称 / 邮箱";

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

        ClearErrors();

        bool hasError = false;

        if (string.IsNullOrWhiteSpace(loginName))
        {
            LoginNameError = IsRegisterMode ? "请填写名称" : "请填写名称或邮箱";
            hasError = true;
        }

        if (IsRegisterMode && string.IsNullOrWhiteSpace(email))
        {
            EmailError = "请填写邮箱";
            hasError = true;
        }

        if (IsRegisterMode && string.IsNullOrWhiteSpace(Password))
        {
            PasswordError = "请填写密码";
            hasError = true;
        }

        if (IsRegisterMode && !string.IsNullOrEmpty(Password) && !string.Equals(Password, ConfirmPassword, System.StringComparison.Ordinal))
        {
            ConfirmPasswordError = "两次输入的密码不一致";
            hasError = true;
        }

        if (hasError)
        {
            return;
        }

        IsSigningIn = true;

        try
        {
            // TODO: 接入真实鉴权；测试阶段统一用 2 秒延时模拟。
            await Task.Delay(2000);

            if (IsRegisterMode)
            {
                await _snapshot.RegisterAsync(loginName, Nickname, email, Password);
                _notifier.Success("注册成功", $"账号「{loginName}」已创建。");
            }
            else
            {
                bool matched = await _snapshot.SignInAsync(loginName, Password);
                _notifier.Success("登录成功", matched
                    ? $"欢迎回来，{_snapshot.Profile.Nickname}。"
                    : $"已以「{_snapshot.Profile.Nickname}」的身份登录。");
            }

            _dialog.Dismiss();
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
