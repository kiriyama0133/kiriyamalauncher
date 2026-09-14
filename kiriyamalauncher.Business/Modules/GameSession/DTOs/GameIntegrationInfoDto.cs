using System.Collections.Generic;

namespace kiriyamalauncher.Business.Modules.GameSession.DTOs;

/// <summary>
/// 某个游戏的联机集成信息（界面用来决定显示哪些控件）。
/// </summary>
public class GameIntegrationInfoDto
{
    /// <summary>集成标识。</summary>
    public string IntegrationId { get; init; } = string.Empty;

    /// <summary>显示名。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>启动程序的默认文件名提示。</summary>
    public string ExecutableFileNameHint { get; init; } = string.Empty;

    /// <summary>会被监视的进程名。</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
}
