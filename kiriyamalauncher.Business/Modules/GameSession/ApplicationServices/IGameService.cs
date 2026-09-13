using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// 提供支持联机的游戏列表。
/// </summary>
public interface IGameService
{
    /// <summary>获取游戏列表。</summary>
    Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken cancellationToken = default);
}
