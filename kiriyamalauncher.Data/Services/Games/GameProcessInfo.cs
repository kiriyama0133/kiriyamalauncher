namespace kiriyamalauncher.Data;

/// <summary>被监视的游戏进程快照。</summary>
/// <param name="ProcessId">进程 ID。</param>
/// <param name="Name">进程名（不含 .exe）。</param>
/// <param name="ExecutablePath">可执行文件路径（拿不到时为空）。</param>
public record GameProcessInfo(int ProcessId, string Name, string ExecutablePath)
{
    /// <summary>给日志 / 界面用的显示文本。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ExecutablePath)
        ? $"{Name}（PID {ProcessId}）"
        : ExecutablePath;
}
