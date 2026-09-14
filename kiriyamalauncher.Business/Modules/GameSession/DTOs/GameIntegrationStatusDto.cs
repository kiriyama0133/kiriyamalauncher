namespace kiriyamalauncher.Business.Modules.GameSession.DTOs;

/// <summary>
/// 某个游戏的联机集成状态（给界面显示用的一次性快照）。
/// </summary>
public class GameIntegrationStatusDto
{
    /// <summary>游戏集成标识。</summary>
    public string IntegrationId { get; init; } = string.Empty;

    /// <summary>是否检测到游戏进程。</summary>
    public bool IsGameRunning { get; init; }

    /// <summary>检测到的游戏进程 ID。</summary>
    public int? ProcessId { get; init; }

    /// <summary>组件是否已经注入。</summary>
    public bool IsInjected { get; init; }

    /// <summary>是否开启了「检测到进程自动注入」。</summary>
    public bool IsAutoInjectEnabled { get; init; }

    /// <summary>状态文字，可直接显示。</summary>
    public string StatusText { get; init; } = string.Empty;
}
