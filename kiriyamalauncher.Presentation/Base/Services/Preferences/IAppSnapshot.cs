using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using System;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Preferences;

/// <summary>
/// 全局快照读写器：应用启动时从 SQLite 恢复偏好并套用到界面，
/// 之后界面上的改动会（防抖地）写回 SQLite。
/// </summary>
public interface IAppSnapshot
{
    /// <summary>当前用户资料与偏好的快照。</summary>
    UserProfileDto Profile { get; }

    /// <summary>是否已经从数据库恢复过。</summary>
    bool IsLoaded { get; }

    /// <summary>偏好发生变化时触发（已经在界面上生效）。</summary>
    event EventHandler? Changed;

    /// <summary>从数据库恢复偏好并套用到界面。</summary>
    Task LoadAsync();

    /// <summary>更新字体大小：立即生效，写库防抖。</summary>
    void UpdateFontSize(double fontSize);

    /// <summary>更新 ZeroTier Moon 服务器地址（写库防抖）。</summary>
    void UpdateMoonServerIp(string moonServerIp);

    /// <summary>更新 ZeroTier 网络 ID（写库防抖）。</summary>
    void UpdateZeroTierNetworkId(string networkId);

    /// <summary>更新 ZeroTier 连接模式（Official / SelfHosted，写库防抖）。</summary>
    void UpdateZeroTierConnectionMode(string connectionMode);

    /// <summary>登录（测试阶段：不校验密码，密码不落库）。返回是否匹配到已注册的账号。</summary>
    Task<bool> SignInAsync(string loginNameOrEmail, string password);

    /// <summary>注册（测试阶段：只把账号信息写进数据库，密码不落库）。</summary>
    Task RegisterAsync(string loginName, string nickname, string email, string password);

    /// <summary>退出登录（清空账号与昵称）。</summary>
    Task SignOutAsync();

    /// <summary>立刻写库。</summary>
    Task SaveAsync();
}
