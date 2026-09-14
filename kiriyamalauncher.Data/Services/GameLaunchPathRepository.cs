using kiriyamalauncher.Data.Entities;
using Microsoft.Data.Sqlite;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IGameLaunchPathRepository"/> 的 SQLite 实现。
/// </summary>
public class GameLaunchPathRepository : IGameLaunchPathRepository
{
    private const string TABLE_SQL = """
        CREATE TABLE IF NOT EXISTS GameLaunchPaths (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            GameId         TEXT    NOT NULL DEFAULT '',
            ExecutablePath TEXT    NOT NULL DEFAULT '',
            CreatedAt      TEXT    NULL,
            UpdatedAt      TEXT    NULL);
        """;

    private const string SELECT_COLUMNS = "Id, GameId, ExecutablePath, CreatedAt, UpdatedAt";

    private readonly SqliteDatabase _database;

    public GameLaunchPathRepository(SqliteDatabase database)
    {
        _database = database;
    }

    /// <inheritdoc />
    public async Task<GameLaunchPath?> FindAsync(string gameId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {SELECT_COLUMNS} FROM GameLaunchPaths WHERE GameId = $gameId ORDER BY Id LIMIT 1;";
        command.Parameters.AddWithValue("$gameId", gameId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(GameLaunchPath path, CancellationToken cancellationToken = default)
    {
        path.UpdatedAt = DateTime.Now;

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using (SqliteCommand updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandText = """
                UPDATE GameLaunchPaths
                SET ExecutablePath = $executablePath,
                    UpdatedAt = $updatedAt
                WHERE GameId = $gameId;
                """;
            updateCommand.Parameters.AddWithValue("$gameId", path.GameId);
            updateCommand.Parameters.AddWithValue("$executablePath", path.ExecutablePath);
            updateCommand.Parameters.AddWithValue("$updatedAt", path.UpdatedAt.Value.ToString("O"));

            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                return;
            }
        }

        await using SqliteCommand insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO GameLaunchPaths (GameId, ExecutablePath, CreatedAt, UpdatedAt)
            VALUES ($gameId, $executablePath, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        insertCommand.Parameters.AddWithValue("$gameId", path.GameId);
        insertCommand.Parameters.AddWithValue("$executablePath", path.ExecutablePath);
        insertCommand.Parameters.AddWithValue("$createdAt", (path.CreatedAt ?? DateTime.Now).ToString("O"));
        insertCommand.Parameters.AddWithValue("$updatedAt", path.UpdatedAt.Value.ToString("O"));

        object? result = await insertCommand.ExecuteScalarAsync(cancellationToken);
        path.Id = (uint)Convert.ToInt64(result);
    }

    private static GameLaunchPath Read(SqliteDataReader reader) => new()
    {
        Id = (uint)reader.GetInt64(0),
        GameId = reader.GetString(1),
        ExecutablePath = reader.GetString(2),
        CreatedAt = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
        UpdatedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4))
    };

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand createCommand = connection.CreateCommand();
        createCommand.CommandText = TABLE_SQL;
        await createCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
