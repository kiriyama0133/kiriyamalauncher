using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;

namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// 每个游戏保存的启动路径（独立一张表，与账号、界面偏好都无关）。
/// </summary>
public class GameLaunchPath : IDataEntity
{
    /// <inheritdoc />
    public uint? Id { get; set; }

    /// <inheritdoc />
    public DateTime? CreatedAt { get; set; }

    /// <summary>游戏集成标识（例如 civ6）。</summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>可执行文件的完整路径。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>更新时间。</summary>
    public DateTime? UpdatedAt { get; set; }
}
