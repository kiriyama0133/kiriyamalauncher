using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;

namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// 用户账号（本机只保存一条当前账号记录）。
///
/// 这里只放账号本身的信息；界面偏好（字号、主题、ZeroTier 等）与账号无关，
/// 单独存在 <see cref="UserPreferences"/> 对应的 Preferences 表里。
/// </summary>
public class User : IDataEntity
{
    /// <inheritdoc />
    public uint? Id { get; set; }

    /// <inheritdoc />
    public DateTime? CreatedAt { get; set; }

    /// <summary>昵称。</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>登录名称。</summary>
    public string LoginName { get; set; } = string.Empty;

    /// <summary>邮箱（注册时填写，也可以用来登录）。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>账号信息更新时间。</summary>
    public DateTime? ProfileUpdatedAt { get; set; }

    /// <summary>访问令牌（JWT，登录后由中继服务器签发）。</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>刷新令牌（用于换取新的访问令牌，登录后由中继服务器签发）。</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>访问令牌过期时间（UTC）。</summary>
    public DateTime? AccessTokenExpiresAt { get; set; }
}
