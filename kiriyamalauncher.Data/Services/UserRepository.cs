using kiriyamalauncher.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 用户账号仓储（Users 表）。
///
/// 说明：老版本的 Users 表里混着偏好列（FontSize / 主题 / ZeroTier 等）。
/// 现在偏好已经搬到独立的 Preferences 表，这里只读写账号字段，
/// 老列保留不动，方便 <see cref="UserPreferencesRepository"/> 首次启动时把它们迁移过去。
/// </summary>
public class UserRepository : IUserRepository
{
    private const string USERS_TABLE_SQL = """
        CREATE TABLE IF NOT EXISTS Users (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Nickname      TEXT    NOT NULL DEFAULT '',
            LoginName     TEXT    NOT NULL DEFAULT '',
            Email         TEXT    NOT NULL DEFAULT '',
            ProfileUpdatedAt TEXT NULL,
            AccessToken   TEXT    NOT NULL DEFAULT '',
            RefreshToken  TEXT    NOT NULL DEFAULT '',
            AccessTokenExpiresAt TEXT NULL,
            CreatedAt     TEXT    NULL);
        """;

    /// <summary>老库需要补的账号列。</summary>
    private static readonly IReadOnlyDictionary<string, string> OPTIONAL_COLUMNS = new Dictionary<string, string>
    {
        ["Email"] = "Email TEXT NOT NULL DEFAULT ''",
        ["AccessToken"] = "AccessToken TEXT NOT NULL DEFAULT ''",
        ["RefreshToken"] = "RefreshToken TEXT NOT NULL DEFAULT ''",
        ["AccessTokenExpiresAt"] = "AccessTokenExpiresAt TEXT NULL"
    };

    private const string SELECT_COLUMNS = "Id, Nickname, LoginName, Email, ProfileUpdatedAt, AccessToken, RefreshToken, AccessTokenExpiresAt, CreatedAt";

    private readonly SqliteDatabase _database;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(SqliteDatabase database, ILogger<UserRepository> logger)
    {
        _database = database;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<User> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using (SqliteCommand selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = $"SELECT {SELECT_COLUMNS} FROM Users ORDER BY Id LIMIT 1;";

            await using SqliteDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadUser(reader);
            }
        }

        // 默认是「未登录」状态：账号信息为空，等用户登录 / 注册后再写入。
        User newUser = new()
        {
            Nickname = string.Empty,
            LoginName = string.Empty,
            Email = string.Empty,
            CreatedAt = DateTime.Now,
            ProfileUpdatedAt = DateTime.Now
        };

        newUser.Id = await InsertAsync(connection, newUser, cancellationToken);
        _logger.LogInformation("已创建默认账号记录（Id = {Id}）。", newUser.Id);

        return newUser;
    }

    /// <inheritdoc />
    public async Task<User?> FindByLoginAsync(string loginNameOrEmail, CancellationToken cancellationToken = default)
    {
        string identifier = loginNameOrEmail.Trim();
        if (identifier.Length == 0)
        {
            return null;
        }

        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SELECT_COLUMNS} FROM Users
            WHERE LoginName = $identifier COLLATE NOCASE OR Email = $identifier COLLATE NOCASE
            ORDER BY Id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$identifier", identifier);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _database.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        if (user.Id is { } id)
        {
            await using SqliteCommand updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE Users
                SET Nickname = $nickname,
                    LoginName = $loginName,
                    Email = $email,
                    ProfileUpdatedAt = $profileUpdatedAt,
                    AccessToken = $accessToken,
                    RefreshToken = $refreshToken,
                    AccessTokenExpiresAt = $accessTokenExpiresAt
                WHERE Id = $id;
                """;
            BindUser(updateCommand, user);
            updateCommand.Parameters.AddWithValue("$id", id);

            if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                return;
            }
        }

        user.Id = await InsertAsync(connection, user, cancellationToken);
    }

    private static async Task<uint> InsertAsync(SqliteConnection connection, User user, CancellationToken cancellationToken)
    {
        await using SqliteCommand insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO Users (Nickname, LoginName, Email, ProfileUpdatedAt, AccessToken, RefreshToken, AccessTokenExpiresAt, CreatedAt)
            VALUES ($nickname, $loginName, $email, $profileUpdatedAt, $accessToken, $refreshToken, $accessTokenExpiresAt, $createdAt);
            SELECT last_insert_rowid();
            """;
        BindUser(insertCommand, user);
        insertCommand.Parameters.AddWithValue("$createdAt", ToDbValue(user.CreatedAt ?? DateTime.Now));

        object? result = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return (uint)Convert.ToInt64(result);
    }

    private static void BindUser(SqliteCommand command, User user)
    {
        command.Parameters.AddWithValue("$nickname", user.Nickname);
        command.Parameters.AddWithValue("$loginName", user.LoginName);
        command.Parameters.AddWithValue("$email", user.Email);
        command.Parameters.AddWithValue("$profileUpdatedAt", ToDbValue(user.ProfileUpdatedAt));
        command.Parameters.AddWithValue("$accessToken", user.AccessToken);
        command.Parameters.AddWithValue("$refreshToken", user.RefreshToken);
        command.Parameters.AddWithValue("$accessTokenExpiresAt", ToDbValue(user.AccessTokenExpiresAt));
    }

    private static object ToDbValue(DateTime? value) => value is null ? DBNull.Value : value.Value.ToString("O");

    private static User ReadUser(SqliteDataReader reader) => new()
    {
        Id = (uint)reader.GetInt64(0),
        Nickname = reader.GetString(1),
        LoginName = reader.GetString(2),
        Email = reader.GetString(3),
        ProfileUpdatedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
        AccessToken = reader.GetString(5),
        RefreshToken = reader.GetString(6),
        AccessTokenExpiresAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
        CreatedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8))
    };

    /// <summary>建表；老库缺列时补上。</summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = USERS_TABLE_SQL;
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        HashSet<string> existingColumns = await SqliteSchema.GetColumnsAsync(connection, "Users", cancellationToken);

        foreach ((string columnName, string columnDefinition) in OPTIONAL_COLUMNS)
        {
            if (existingColumns.Contains(columnName))
            {
                continue;
            }

            await using SqliteCommand alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE Users ADD COLUMN {columnDefinition};";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
