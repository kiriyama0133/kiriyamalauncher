using System;
using System.Collections.Generic;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏进程监视：主动轮询系统进程列表，检测游戏进程是否出现 / 退出。
///
/// 这里刻意与具体游戏无关：进程名由调用方给（来自 <see cref="IGameIntegration.ProcessNames"/>），
/// 以后加别的游戏不用改这段代码。
/// 事件在后台线程上触发，界面层需要自己切回 UI 线程。
/// </summary>
public interface IGameProcessMonitor
{
    /// <summary>检测到游戏进程启动。</summary>
    event EventHandler<GameProcessInfo>? ProcessStarted;

    /// <summary>检测到游戏进程退出。</summary>
    event EventHandler<GameProcessInfo>? ProcessStopped;

    /// <summary>当前正在监视的进程名（不含 .exe）。</summary>
    IReadOnlyList<string> WatchedNames { get; }

    /// <summary>当前匹配到的进程。</summary>
    IReadOnlyList<GameProcessInfo> CurrentProcesses { get; }

    /// <summary>开始监视这些进程名（切换游戏时重复调用即可）。</summary>
    void Watch(IEnumerable<string> processNames);

    /// <summary>停止监视（不会影响已经运行的游戏进程）。</summary>
    void StopWatching();

    /// <summary>立刻扫描一次，不改变监视状态（用于「立即注入」这类手动操作）。</summary>
    IReadOnlyList<GameProcessInfo> Scan(IEnumerable<string> processNames);
}
