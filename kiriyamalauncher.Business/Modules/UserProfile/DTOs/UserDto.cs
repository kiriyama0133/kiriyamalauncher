using System;

namespace kiriyamalauncher.Business.Modules.UserProfile.DTOs;

/// <summary>
/// 用户账号（与界面偏好无关）。
/// </summary>
public class UserDto
{
    /// <summary>数据库主键。</summary>
    public uint? Id { get; set; }

    /// <summary>昵称。</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>登录名称。</summary>
    public string LoginName { get; set; } = string.Empty;

    /// <summary>邮箱（注册时填写，也可以用来登录）。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>创建时间。</summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>账号信息更新时间。</summary>
    public DateTime? ProfileUpdatedAt { get; set; }

    /// <summary>访问令牌（JWT，登录后由中继服务器签发，不落库明文密码）。</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>刷新令牌（用于换取新的访问令牌）。</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>访问令牌过期时间（UTC）。</summary>
    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>是否已登录（有访问令牌）。</summary>
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(AccessToken);
}
