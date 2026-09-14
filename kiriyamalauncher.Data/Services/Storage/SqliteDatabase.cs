using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Core;
using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// SQLite 连接提供器：解析数据库文件位置并缓存连接字符串。
/// 工具箱返回的可能是数据库文件，也可能只是存放目录，这里统一处理。
/// </summary>
public class SqliteDatabase
{
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private string? _connectionString;

    public SqliteDatabase(IDatabaseInitializer databaseInitializer, ILogger<SqliteDatabase> logger)
    {
        _databaseInitializer = databaseInitializer;
        _logger = logger;
    }

    /// <summary>打开一个可用的数据库连接（表结构由各个仓储自己保证）。</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        string connectionString = await GetConnectionStringAsync(cancellationToken);

        SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is not null)
        {
            return _connectionString;
        }

        await _connectionLock.WaitAsync(cancellationToken);

        try
        {
            return _connectionString ??= BuildConnectionString();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private string BuildConnectionString()
    {
        _databaseInitializer.InitializeDatabase();

        ProcessResult<string> pathResult = _databaseInitializer.GetDBPath();
        string path = pathResult.ValueOrDefault ?? string.Empty;

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
