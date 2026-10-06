using Microsoft.Data.Sqlite;

namespace Infrastructure;

/// <summary>
/// SQLite 单例连接工厂。多实例并发安全：
/// WAL 日志模式 + busy_timeout=5000ms + SQLITE_BUSY 指数退避重试 3 次。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(dataDirectory, "history.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        DbRetry.Execute(() =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA foreign_keys=ON;
                """;
            cmd.ExecuteNonQuery();
        });
        return conn;
    }
}

/// <summary>SQLITE_BUSY / SQLITE_LOCKED 指数退避重试（3 次）。</summary>
public static class DbRetry
{
    public static T Execute<T>(Func<T> action)
    {
        const int maxAttempts = 3; // 初次 + 重试 3 次
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < maxAttempts)
            {
                Thread.Sleep(100 * (1 << attempt)); // 100ms / 200ms / 400ms
            }
        }
    }

    public static void Execute(Action action)
        => Execute<object?>(() => { action(); return null; });

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        const int maxAttempts = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < maxAttempts)
            {
                await Task.Delay(100 * (1 << attempt)).ConfigureAwait(false);
            }
        }
    }

    private static bool IsBusy(SqliteException ex)
        => ex.SqliteErrorCode == 5 /* SQLITE_BUSY */ || ex.SqliteErrorCode == 6 /* SQLITE_LOCKED */;
}
