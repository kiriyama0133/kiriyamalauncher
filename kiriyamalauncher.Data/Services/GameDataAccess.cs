using kiriyamalauncher.Data.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏类别数据访问。
///
/// 目前返回内置的示例数据（只放了一个文明 6，其它的后面补充）；
/// 后续接入真实来源（后端接口或本地 SQLite）时，只需要替换这个类的实现。
/// </summary>
public class GameDataAccess : IGameDataAccess
{
    private static readonly IReadOnlyList<Game> Games =
    [
        new Game
        {
            Name = "文明 6",
            Description = "回合制策略，一局到天亮",
            CoverImage = "sid.jpeg",
            IntegrationId = Civilization6GameIntegration.INTEGRATION_ID
        }
    ];

    public Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Games);
    }
}
