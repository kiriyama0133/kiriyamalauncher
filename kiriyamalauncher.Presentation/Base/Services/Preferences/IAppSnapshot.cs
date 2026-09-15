using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using System;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Preferences;

/// <summary>
/// 全局快照读写器：应用启动时从 SQLite 恢复账号与偏好并套用到界面，
/// 之后界面上的改动会写回 SQLite。
///
/// 账号（Users 表）与偏好（Preferences 表）是两份互相独立的数据：
/// 退出登录不会动偏好，换账号也不会覆盖偏好。
/// </summary>
public interface IAppSnapshot
{
    /// <summary>当前账号。</summary>
    UserDto User { get; }

    /// <summary>当前界面与联机偏好。</summary>
    UserPreferencesDto Preferences { get; }

    /// <summary>是否已经从数据库恢复过。</summary>
    bool IsLoaded { get; }

    /// <summary>账号或偏好发生变化时触发（已经在界面上生效）。</summary>
    event EventHandler? Changed;

    /// <summary>从数据库恢复账号与偏好，并把偏好套用到界面。</summary>
    Task LoadAsync();

    /// <summary>更新字体大小：立即生效，写库防抖（滑块会连续变化）。</summary>
    void UpdateFontSize(double fontSize);

    /// <summary>更新 ZeroTier Moon 服务器节点 ID（立即写库）。</summary>
    void UpdateMoonServerIp(string moonServerIp);

    /// <summary>更新 ZeroTier 网络 ID（立即写库）。</summary>
    void UpdateZeroTierNetworkId(string networkId);

    /// <summary>更新自建控制器生成的网络 ID（立即写库）。</summary>
    void UpdateSelfHostedNetworkId(string networkId);

    /// <summary>更新 ZeroTier 连接模式（Official / SelfHosted / Relay，立即写库）。</summary>
    void UpdateZeroTierConnectionMode(string connectionMode);

    /// <summary>更新中继服务器（服务端联机）的 IP 或主机名（立即写库）。</summary>
    void UpdateRelayServerIp(string relayServerIp);

    /// <summary>更新中继服务器（服务端联机）的端口（立即写库）。</summary>
    void UpdateRelayServerPort(int relayServerPort);

    /// <summary>更新 ZeroTier 传输引擎（Sockets / Client，立即写库）。</summary>
    void UpdateZeroTierTransportBackend(string transportBackend);

    /// <summary>记录明暗模式（调用方已经切换过 SukiUI 主题，这里只写库）。</summary>
    void UpdateBaseTheme(string baseTheme);

    /// <summary>记录配色主题（调用方已经切换过 SukiUI 配色，这里只写库）。</summary>
    void UpdateColorTheme(string colorTheme);

    /// <summary>
    /// 登录（PKCE + OAuth）：调中继服务器校验密码并换取访问令牌。
    /// 成功会把账号信息与令牌写入本地；失败抛 <see cref="kiriyamalauncher.Data.RelayServerException"/>。
    /// </summary>
    Task SignInAsync(string email, string password);

    /// <summary>
    /// 注册：调中继服务器创建账户。邮箱已被占用会抛 <see cref="kiriyamalauncher.Data.RelayServerException"/>。
    /// </summary>
    Task RegisterAsync(string email, string password, string displayName);

    /// <summary>退出登录（清空账号与昵称，偏好不受影响）。</summary>
    Task SignOutAsync();

    /// <summary>立刻把未保存的内容写库。</summary>
    Task SaveAsync();

    /// <summary>等待正在防抖中的写库结束（应用退出前调用）。</summary>
    Task FlushAsync();
}
