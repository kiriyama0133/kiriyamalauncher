using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;

namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// 用户资料与偏好设置（本机只保存一条当前用户记录）。
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

    /// <summary>资料更新时间。</summary>
    public DateTime? ProfileUpdatedAt { get; set; }

    /// <summary>界面字体大小。</summary>
    public double FontSize { get; set; } = 14d;

    /// <summary>ZeroTier Moon 服务器地址（IP 或域名）。</summary>
    public string MoonServerIp { get; set; } = string.Empty;

    /// <summary>ZeroTier 网络 ID（16 位十六进制）。</summary>
    public string ZeroTierNetworkId { get; set; } = string.Empty;

    /// <summary>ZeroTier 连接模式：Official（my.zerotier.com）/ SelfHosted（自建控制器）。</summary>
    public string ZeroTierConnectionMode { get; set; } = ZeroTierSettings.OfficialController;

    /// <summary>明暗模式：Default（跟随系统）/ Light / Dark。</summary>
    public string BaseTheme { get; set; } = "Default";

    /// <summary>配色主题：Blue / Green / Red / Orange。</summary>
    public string ColorTheme { get; set; } = "Blue";
}
