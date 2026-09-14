using kiriyamalauncher.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 界面偏好仓储（Preferences 表，整机单行，与账号无关）。
///
/// 第一次创建时会尝试把老版本 Users 表里的偏好列搬过来，
/// 避免升级之后字号 / 主题 / ZeroTier 配置被重置。
/// </summary>
public class UserPreferencesRepository : IUserPreferencesRepository
{
    private const string PREFERENCES_TABLE_SQL = """
        CREATE TABLE IF NOT EXISTS Preferences (
            Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
            FontSize               REAL    NOT NULL DEFAULT 14,
            MoonServerIp           TEXT    NOT NULL DEFAULT '',
            ZeroTierNetworkId      TEXT    NOT NULL DEFAULT '',
            ZeroTierConnectionMode TEXT    NOT NULL DEFAULT 'Official',
            BaseTheme              TEXT    NOT NULL DEFAULT 'Default',
            ColorTheme             TEXT    NOT NULL DEFAULT 'Blue',
            CreatedAt              TEXT    NULL,
            UpdatedAt              TEXT    NULL);
        """;

    /// <summary>老库需要补的列（列名 → 建列语句片段）。</summary>
    private static readonly IReadOnlyDictionary<string, string> OPTIONAL_COLUMNS = new Dictionary<string, string>
    {
        ["CreatedAt"] = "CreatedAt TEXT NULL"
    };

    /// <summary>老版本 Users 表里曾经存放偏好的列。</summary>
    private static readonly string[] LEGACY_COLUMNS =
    [
        "FontSize", "MoonServerIp", "ZeroTierNetworkId", "ZeroTierConnectionMode", "BaseTheme", "ColorTheme"
    ];

    private const string SELECT_COLUMNS =
        "Id, FontSize, MoonServerIp, ZeroTierNetworkId, ZeroTierConnectionMode, BaseTheme, ColorTheme, CreatedAt, UpdatedAt";

    private readonly SqliteDatabase _database;
    private readonly ILogger<UserPreferencesRepository> _logger;

    public UserPreferencesRepository(SqliteDatabase database, ILogger<UserPreferencesRepository> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserPreferences> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using (SqliteCommand selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = $"SELECT {SELECT_COLUMNS} FROM Preferences ORDER BY Id LIMIT 1;";

            await using SqliteDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadPreferences(reader);
            }
        }

        UserPreferences preferences = new()
        {
            FontSize = 14d,
            MoonServerIp = string.Empty,
            ZeroTierNetworkId = string.Empty,
            ZeroTierConnectionMode = ZeroTierSettings.OfficialController,
            BaseTheme = "Default",
            ColorTheme = "Blue",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        await MigrateLegacyAsync(connection, preferences, cancellationToken);

        preferences.Id = await InsertAsync(connection, preferences, cancellationToken);
        _logger.LogInformation(
            "已创建偏好记录（Id = {Id}，字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}）。",
            preferences.Id, preferences.FontSize, preferences.BaseTheme, preferences.ColorTheme);

        return preferences;
    }

    /// <inheritdoc />
    public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        preferences.UpdatedAt = DateTime.Now;

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        if (preferences.Id is { } id)
        {
            await using SqliteCommand updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE Preferences
                SET FontSize = $fontSize,
                    MoonServerIp = $moonServerIp,
                    ZeroTierNetworkId = $zeroTierNetworkId,
                    ZeroTierConnectionMode = $zeroTierConnectionMode,
                    BaseTheme = $baseTheme,
                    ColorTheme = $colorTheme,
                    UpdatedAt = $updatedAt
                WHERE Id = $id;
                """;
            BindPreferences(updateCommand, preferences);
            updateCommand.Parameters.AddWithValue("$id", id);

            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                return;
            }
        }

        preferences.Id = await InsertAsync(connection, preferences, cancellationToken);
    }

    private static async Task<uint> InsertAsync(SqliteConnection connection, UserPreferences preferences, CancellationToken cancellationToken)
    {
        await using SqliteCommand insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO Preferences (FontSize, MoonServerIp, ZeroTierNetworkId, ZeroTierConnectionMode, BaseTheme, ColorTheme, CreatedAt, UpdatedAt)
            VALUES ($fontSize, $moonServerIp, $zeroTierNetworkId, $zeroTierConnectionMode, $baseTheme, $colorTheme, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        BindPreferences(insertCommand, preferences);

        object? result = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return (uint)Convert.ToInt64(result);
    }

    private static void BindPreferences(SqliteCommand command, UserPreferences preferences)
    {
        command.Parameters.AddWithValue("$fontSize", preferences.FontSize);
        command.Parameters.AddWithValue("$moonServerIp", preferences.MoonServerIp);
        command.Parameters.AddWithValue("$zeroTierNetworkId", preferences.ZeroTierNetworkId);
        command.Parameters.AddWithValue("$zeroTierConnectionMode", preferences.ZeroTierConnectionMode);
        command.Parameters.AddWithValue("$baseTheme", preferences.BaseTheme);
        command.Parameters.AddWithValue("$colorTheme", preferences.ColorTheme);
        command.Parameters.AddWithValue("$createdAt", preferences.CreatedAt is { } createdAt ? createdAt.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", preferences.UpdatedAt is { } updatedAt ? updatedAt.ToString("O") : DBNull.Value);
    }

    private static UserPreferences ReadPreferences(SqliteDataReader reader) => new()
    {
        Id = (uint)reader.GetInt64(0),
        FontSize = reader.GetDouble(1),
        MoonServerIp = reader.GetString(2),
        ZeroTierNetworkId = reader.GetString(3),
        ZeroTierConnectionMode = reader.GetString(4),
        BaseTheme = reader.GetString(5),
        ColorTheme = reader.GetString(6),
        CreatedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
        UpdatedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8))
    };

    /// <summary>建表；旧库缺列时补上。</summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = PREFERENCES_TABLE_SQL;
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        HashSet<string> existingColumns = await SqliteSchema.GetColumnsAsync(connection, "Preferences", cancellationToken);

        foreach ((string columnName, string columnDefinition) in OPTIONAL_COLUMNS)
        {
            if (existingColumns.Contains(columnName))
            {
                continue;
            }

            await using SqliteCommand alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE Preferences ADD COLUMN {columnDefinition};";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>把老版本 Users 表里的偏好列搬到新的偏好对象上（表 / 列不存在就跳过）。</summary>
    private async Task MigrateLegacyAsync(SqliteConnection connection, UserPreferences preferences, CancellationToken cancellationToken)
    {
        HashSet<string> userColumns = await SqliteSchema.GetColumnsAsync(connection, "Users", cancellationToken);
        if (userColumns.Count == 0)
        {
            return;
        }

        List<string> available = [];
        foreach (string column in LEGACY_COLUMNS)
        {
            if (userColumns.Contains(column))
            {
                available.Add(column);
            }
        }

        if (available.Count == 0)
        {
            return;
        }

        string projection = string.Join(", ", available);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {projection} FROM Users ORDER BY Id LIMIT 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        double fontSize = ReadDouble(reader, available.IndexOf("FontSize"), preferences.FontSize);
        preferences.FontSize = fontSize is >= 12d and <= 22d ? fontSize : preferences.FontSize;
        preferences.MoonServerIp = ReadString(reader, available.IndexOf("MoonServerIp"), preferences.MoonServerIp);
        preferences.ZeroTierNetworkId = ReadString(reader, available.IndexOf("ZeroTierNetworkId"), preferences.ZeroTierNetworkId);
        preferences.ZeroTierConnectionMode = ReadString(reader, available.IndexOf("ZeroTierConnectionMode"), preferences.ZeroTierConnectionMode);
        preferences.BaseTheme = ReadString(reader, available.IndexOf("BaseTheme"), preferences.BaseTheme);
        preferences.ColorTheme = ReadString(reader, available.IndexOf("ColorTheme"), preferences.ColorTheme);

        _logger.LogInformation(
            "已从旧版 Users 表迁移偏好：字号 {FontSize}，主题 {BaseTheme}/{ColorTheme}。",
            preferences.FontSize, preferences.BaseTheme, preferences.ColorTheme);
    }

    private static string ReadString(SqliteDataReader reader, int index, string fallback)
        => index < 0 || reader.IsDBNull(index) ? fallback : (string.IsNullOrWhiteSpace(reader.GetString(index)) ? fallback : reader.GetString(index));

    private static double ReadDouble(SqliteDataReader reader, int index, double fallback)
        => index < 0 || reader.IsDBNull(index) ? fallback : reader.GetDouble(index);
}
