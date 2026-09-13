using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// 游戏列表服务：从数据层取游戏，转换成业务层 DTO 交给表现层。
/// </summary>
public class GameService : IGameService
{
    private readonly IGameDataAccess _gameDataAccess;

    public GameService(IGameDataAccess gameDataAccess)
    {
        _gameDataAccess = gameDataAccess;
    }

    public async Task<IReadOnlyList<GameDto>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Game> games = await _gameDataAccess.GetGamesAsync(cancellationToken);

        return games
            .Select(game => new GameDto
            {
                Name = game.Name,
                Description = game.Description,
                CoverImage = game.CoverImage
            })
            .ToList();
    }
}
