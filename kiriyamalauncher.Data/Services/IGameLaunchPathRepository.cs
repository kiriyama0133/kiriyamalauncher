using kiriyamalauncher.Data.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏启动路径仓储（GameLaunchPaths 表，每个游戏一条记录）。
/// </summary>
public interface IGameLaunchPathRepository
{
    /// <summary>取某个游戏的启动路径；没设置过返回 null。</summary>
    Task<GameLaunchPath?> FindAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>写入启动路径（按 GameId 更新或插入）。</summary>
    Task SaveAsync(GameLaunchPath path, CancellationToken cancellationToken = default);
}
