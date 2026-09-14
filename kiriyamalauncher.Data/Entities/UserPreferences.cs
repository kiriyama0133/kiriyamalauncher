using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;

namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// 界面与联机偏好（独立于账号，整机一份，存在 Preferences 表里）。
/// </summary>
public class UserPreferences : IDataEntity
{
    /// <inheritdoc />
    public uint? Id { get; set; }

    /// <inheritdoc />
    public DateTime? CreatedAt { get; set; }

    /// <summary>界面字体大小（基准字号）。</summary>
    public double FontSize { get; set; } = 14d;

    /// <summary>ZeroTier Moon 服务器节点 ID。</summary>
    public string MoonServerIp { get; set; } = string.Empty;

    /// <summary>ZeroTier 网络 ID（16 位十六进制）。</summary>
    public string ZeroTierNetworkId { get; set; } = string.Empty;

    /// <summary>ZeroTier 连接模式：Official（my.zerotier.com）/ SelfHosted（自建控制器）。</summary>
    public string ZeroTierConnectionMode { get; set; } = ZeroTierSettings.OfficialController;

    /// <summary>明暗模式：Default（跟随系统）/ Light / Dark。</summary>
    public string BaseTheme { get; set; } = "Default";

    /// <summary>配色主题，存 SukiUI 的内置配色名（Blue / Green / Red / Orange）。</summary>
    public string ColorTheme { get; set; } = "Blue";

    /// <summary>偏好更新时间。</summary>
    public DateTime? UpdatedAt { get; set; }
}
