using kiriyamalauncher.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Core;
using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 基于 SQLite 的用户仓储。
///
/// 表结构在这里按需创建（CREATE TABLE IF NOT EXISTS），并对旧库自动补列。
/// 注意：测试阶段只保存账号与偏好信息，密码一律不落库。
/// </summary>
public class UserRepository : IUserRepository
{
    /// <summary>表结构；升级新增列时同时补进 <see cref="EnsureSchemaAsync"/> 的补列清单。</summary>
    private const string USERS_TABLE_SQL = """
        CREATE TABLE IF NOT EXISTS Users (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            Nickname      TEXT    NOT NULL DEFAULT '',
            LoginName     TEXT    NOT NULL DEFAULT '',
            Email         TEXT    NOT NULL DEFAULT '',
            ProfileUpdatedAt TEXT NULL,
            CreatedAt     TEXT    NULL,
            FontSize      REAL    NOT NULL DEFAULT 14,
            BaseTheme     TEXT    NOT NULL DEFAULT 'Default',
            ColorTheme    TEXT    NOT NULL DEFAULT 'Blue',
            MoonServerIp  TEXT    NOT NULL DEFAULT '',
            ZeroTierNetworkId TEXT NOT NULL DEFAULT '',
            ZeroTierConnectionMode TEXT NOT NULL DEFAULT 'Official');
        """;

    /// <summary>旧库需要补的列（列名 → 建列语句片段）。</summary>
    private static readonly IReadOnlyDictionary<string, string> OPTIONAL_COLUMNS = new Dictionary<string, string>
    {
        ["Email"] = "Email TEXT NOT NULL DEFAULT ''",
        ["MoonServerIp"] = "MoonServerIp TEXT NOT NULL DEFAULT ''",
        ["ZeroTierNetworkId"] = "ZeroTierNetworkId TEXT NOT NULL DEFAULT ''",
        ["ZeroTierConnectionMode"] = "ZeroTierConnectionMode TEXT NOT NULL DEFAULT 'Official'"
    };

    private const string SELECT_SQL = """
        SELECT Id, Nickname, LoginName, Email, MoonServerIp, ZeroTierNetworkId, ZeroTierConnectionMode,
               ProfileUpdatedAt, CreatedAt, FontSize, BaseTheme, ColorTheme
        FROM Users ORDER BY Id LIMIT 1;
        """;

    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly ILogger<UserRepository> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private string? _connectionString;

    public UserRepository(IDatabaseInitializer databaseInitializer, ILogger<UserRepository> logger)
    {
        _databaseInitializer = databaseInitializer;
        _logger = logger;
    }

    public async Task<User> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand selectCommand = connection.CreateCommand();
        selectCommand.CommandText = SELECT_SQL;

        await using SqliteDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadUser(reader);
        }

        await reader.DisposeAsync();

        // 默认是「未登录」状态：账号信息为空，等用户登录 / 注册后再写入。
        User newUser = new()
        {
            Nickname = string.Empty,
            LoginName = string.Empty,
            Email = string.Empty,
            MoonServerIp = string.Empty,
            CreatedAt = DateTime.Now,
            ProfileUpdatedAt = DateTime.Now
        };

        newUser.Id = await InsertAsync(connection, newUser, cancellationToken);
        _logger.LogInformation("已创建默认用户记录（Id = {Id}）。", newUser.Id);

        return newUser;
    }

    public async Task SaveAsync(User user, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE Users
            SET Nickname = $nickname,
                LoginName = $loginName,
                Email = $email,
                ProfileUpdatedAt = $profileUpdatedAt,
                FontSize = $fontSize,
                BaseTheme = $baseTheme,
                ColorTheme = $colorTheme,
                MoonServerIp = $moonServerIp,
                ZeroTierNetworkId = $zeroTierNetworkId,
                ZeroTierConnectionMode = $zeroTierConnectionMode
            WHERE Id = $id;
            """;
        BindUser(updateCommand, user);
        updateCommand.Parameters.AddWithValue("$id", user.Id ?? 0);

        int affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            user.Id = await InsertAsync(connection, user, cancellationToken);
        }
    }

    private static async Task<uint> InsertAsync(SqliteConnection connection, User user, CancellationToken cancellationToken)
    {
        await using SqliteCommand insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO Users (Nickname, LoginName, Email, MoonServerIp, ZeroTierNetworkId, ZeroTierConnectionMode,
                               ProfileUpdatedAt, CreatedAt, FontSize, BaseTheme, ColorTheme)
            VALUES ($nickname, $loginName, $email, $moonServerIp, $zeroTierNetworkId, $zeroTierConnectionMode,
                    $profileUpdatedAt, $createdAt, $fontSize, $baseTheme, $colorTheme);
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
        command.Parameters.AddWithValue("$moonServerIp", user.MoonServerIp);
        command.Parameters.AddWithValue("$zeroTierNetworkId", user.ZeroTierNetworkId);
        command.Parameters.AddWithValue("$zeroTierConnectionMode", user.ZeroTierConnectionMode);
        command.Parameters.AddWithValue("$profileUpdatedAt", ToDbValue(user.ProfileUpdatedAt));
        command.Parameters.AddWithValue("$fontSize", user.FontSize);
        command.Parameters.AddWithValue("$baseTheme", user.BaseTheme);
        command.Parameters.AddWithValue("$colorTheme", user.ColorTheme);
    }

    private static object ToDbValue(DateTime? value) => value is null ? DBNull.Value : value.Value.ToString("O");

    private static User ReadUser(SqliteDataReader reader) => new()
    {
        Id = (uint)reader.GetInt64(0),
        Nickname = reader.GetString(1),
        LoginName = reader.GetString(2),
        Email = reader.GetString(3),
        MoonServerIp = reader.GetString(4),
        ZeroTierNetworkId = reader.GetString(5),
        ZeroTierConnectionMode = reader.GetString(6),
        ProfileUpdatedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
        CreatedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
        FontSize = reader.GetDouble(9),
        BaseTheme = reader.GetString(10),
        ColorTheme = reader.GetString(11)
    };

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is null)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                _connectionString ??= BuildConnectionString();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureSchemaAsync(connection, cancellationToken);

        return connection;
    }

    /// <summary>建表；如果是从旧版本升级上来的库，补上缺失的列。</summary>
    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (SqliteCommand createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = USERS_TABLE_SQL;
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        HashSet<string> existingColumns = new(StringComparer.OrdinalIgnoreCase);

        await using (SqliteCommand pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = "PRAGMA table_info(Users);";
            await using SqliteDataReader reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

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

    private string BuildConnectionString()
    {
        _databaseInitializer.InitializeDatabase();

        ProcessResult<string> pathResult = _databaseInitializer.GetDBPath();
        string path = pathResult.ValueOrDefault ?? string.Empty;

        // 工具箱返回的可能是数据库文件，也可能只是存放目录。
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kiriyamalauncher", "data.db");
        }
        else if (Directory.Exists(path) || !Path.HasExtension(path))
        {
            path = Path.Combine(path, "data.db");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("SQLite 数据库：{Path}", path);

        return new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }
}
