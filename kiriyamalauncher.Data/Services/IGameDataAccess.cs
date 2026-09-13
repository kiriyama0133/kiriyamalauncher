using kiriyamalauncher.Data.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏类别数据访问。
/// </summary>
public interface IGameDataAccess
{
    /// <summary>获取支持联机的游戏列表。</summary>
    Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken cancellationToken = default);
}
