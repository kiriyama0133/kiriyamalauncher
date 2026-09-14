using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data.Entities;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using Material.Icons;
using System;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 设置页：字体大小、ZeroTier Moon 服务器、网络 ID 与连接模式。
/// 改动会立即生效，并通过全局快照读写器（防抖）写回 SQLite。
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    private readonly IAppSnapshot _snapshot;

    /// <summary>正在从快照同步连接模式时，避免两个单选按钮来回触发。</summary>
    private bool _isSyncingConnectionMode;

    public override string DisplayName => "设置";

    public override MaterialIconKind Icon => MaterialIconKind.CogOutline;

    /// <summary>可设置的最小字号。</summary>
    public double MinimumFontSize => 12d;

    /// <summary>可设置的最大字号。</summary>
    public double MaximumFontSize => 22d;

    [ObservableProperty]
    private double _fontSize = 14d;

    /// <summary>正在编辑的 ZeroTier Moon 服务器地址。</summary>
    [ObservableProperty]
    private string _moonServerIp = string.Empty;

    /// <summary>Moon 输入框聚焦时才显示「确认 / 取消」。</summary>
    [ObservableProperty]
    private bool _isEditingMoonServer;

    /// <summary>正在编辑的 ZeroTier 网络 ID。</summary>
    [ObservableProperty]
    private string _zeroTierNetworkId = string.Empty;

    /// <summary>网络 ID 输入框聚焦时才显示「确认 / 取消」。</summary>
    [ObservableProperty]
    private bool _isEditingNetworkId;

    /// <summary>网络 ID 的校验错误提示（为空表示没有错误）。</summary>
    [ObservableProperty]
    private string _networkIdError = string.Empty;

    /// <summary>是否有网络 ID 校验错误。</summary>
    public bool HasNetworkIdError => !string.IsNullOrEmpty(NetworkIdError);

    /// <summary>连接模式：是否使用 my.zerotier.com 官方控制器。</summary>
    [ObservableProperty]
    private bool _isOfficialController = true;

    /// <summary>连接模式：是否使用自建控制器。</summary>
    [ObservableProperty]
    private bool _isSelfHostedController;

    /// <summary>当前连接模式的说明文字。</summary>
    public string ConnectionModeHint => IsSelfHostedController
        ? "自建控制器：网络由你自己的 zerotier-one controller 提供（moon 只负责根节点与中继），网络 ID 填自建控制器生成的 16 位 ID。"
        : "my.zerotier.com：在官网创建网络后，把 16 位网络 ID 填在下面；走官方根节点，不需要 moon。若是私有网络，记得在官网把新成员（节点 ID）勾选 Auth。";

    /// <summary>上一次保存的 Moon 服务器地址（用于「取消」还原）。</summary>
    private string _savedMoonServerIp = string.Empty;

    /// <summary>上一次保存的网络 ID（用于「取消」还原）。</summary>
    private string _savedNetworkId = string.Empty;

    public SettingsViewModel(IAppSnapshot snapshot)
    {
        _snapshot = snapshot;
        _fontSize = snapshot.Preferences.FontSize;
        _moonServerIp = snapshot.Preferences.MoonServerIp;
        _savedMoonServerIp = _moonServerIp;
        _zeroTierNetworkId = snapshot.Preferences.ZeroTierNetworkId;
        _savedNetworkId = _zeroTierNetworkId;
        SyncConnectionMode();

        snapshot.Changed += OnSnapshotChanged;
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        FontSize = _snapshot.Preferences.FontSize;
        SyncConnectionMode();

        // 正在编辑时不要覆盖用户输入。
        if (!IsEditingMoonServer)
        {
            MoonServerIp = _snapshot.Preferences.MoonServerIp;
            _savedMoonServerIp = MoonServerIp;
        }

        if (!IsEditingNetworkId)
        {
            ZeroTierNetworkId = _snapshot.Preferences.ZeroTierNetworkId;
            _savedNetworkId = ZeroTierNetworkId;
        }
    }

    partial void OnFontSizeChanged(double value) => _snapshot.UpdateFontSize(value);

    partial void OnNetworkIdErrorChanged(string value) => OnPropertyChanged(nameof(HasNetworkIdError));

    partial void OnIsOfficialControllerChanged(bool value)
    {
        if (_isSyncingConnectionMode || !value)
        {
            return;
        }

        ApplyConnectionMode(ZeroTierSettings.OfficialController);
    }

    partial void OnIsSelfHostedControllerChanged(bool value)
    {
        if (_isSyncingConnectionMode || !value)
        {
            return;
        }

        ApplyConnectionMode(ZeroTierSettings.SelfHostedController);
    }

    /// <summary>确认：写入 Moon 服务器地址（防抖落库）。</summary>
    [RelayCommand]
    private void SaveMoonServer()
    {
        _snapshot.UpdateMoonServerIp(MoonServerIp);

        _savedMoonServerIp = _snapshot.Preferences.MoonServerIp;
        MoonServerIp = _savedMoonServerIp;
        IsEditingMoonServer = false;
    }

    /// <summary>取消：还原成上一次保存的 Moon 服务器地址。</summary>
    [RelayCommand]
    private void CancelMoonServer()
    {
        MoonServerIp = _savedMoonServerIp;
        IsEditingMoonServer = false;
    }

    /// <summary>确认：校验后写入网络 ID（防抖落库）。</summary>
    [RelayCommand]
    private void SaveNetworkId()
    {
        string value = (ZeroTierNetworkId ?? string.Empty).Trim().ToLowerInvariant();

        if (!ZeroTierSettings.IsValidNetworkId(value))
        {
            NetworkIdError = "网络 ID 需要是 16 位十六进制，例如 633e31d8a2724274。";
            return;
        }

        NetworkIdError = string.Empty;
        _snapshot.UpdateZeroTierNetworkId(value);

        _savedNetworkId = _snapshot.Preferences.ZeroTierNetworkId;
        ZeroTierNetworkId = _savedNetworkId;
        IsEditingNetworkId = false;
    }

    /// <summary>取消：还原成上一次保存的网络 ID。</summary>
    [RelayCommand]
    private void CancelNetworkId()
    {
        ZeroTierNetworkId = _savedNetworkId;
        NetworkIdError = string.Empty;
        IsEditingNetworkId = false;
    }

    /// <summary>切换连接模式：写进偏好（防抖落库）。</summary>
    private void ApplyConnectionMode(string connectionMode)
    {
        _snapshot.UpdateZeroTierConnectionMode(connectionMode);
        SyncConnectionMode();
    }

    /// <summary>把快照里的连接模式同步到两个单选按钮上（不触发回写）。</summary>
    private void SyncConnectionMode()
    {
        bool isSelfHosted = string.Equals(
            _snapshot.Preferences.ZeroTierConnectionMode,
            ZeroTierSettings.SelfHostedController,
            StringComparison.OrdinalIgnoreCase);

        _isSyncingConnectionMode = true;
        IsSelfHostedController = isSelfHosted;
        IsOfficialController = !isSelfHosted;
        _isSyncingConnectionMode = false;

        OnPropertyChanged(nameof(ConnectionModeHint));
    }
}
