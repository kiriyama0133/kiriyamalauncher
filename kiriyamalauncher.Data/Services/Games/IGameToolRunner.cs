using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 外部小工具的运行器：把「怎么起一个进程」和「这个游戏要起哪个工具」解耦。
/// </summary>
public interface IGameToolRunner
{
    /// <summary>
    /// 运行一个随应用发行的小工具（不弹控制台窗口），等待它结束并返回结果。
    /// 工具超过 <paramref name="timeout"/> 还没退出时按「已启动」处理（有些工具会弹确认框）。
    /// </summary>
    Task<GameToolResult> RunAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        System.TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
