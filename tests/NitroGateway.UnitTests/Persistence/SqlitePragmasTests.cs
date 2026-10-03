using Microsoft.Data.Sqlite;
using NitroGateway.Persistence.Sqlite;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// SqlitePragmas.Apply：WAL（库级，首次设置后缓存）+ synchronous/busy_timeout（连接级，每次执行）。
/// 用临时文件库验证，WAL 不适用于 :memory:。
/// </summary>
public class SqlitePragmasTests
{
    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"ntg-pragma-{Guid.NewGuid():N}.db");

    [Fact]
    public void Apply_SetsWalAndConnectionPragmas()
    {
        var path = TempPath();
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();

            SqlitePragmas.Apply(connection);

            Assert.Equal("wal", ScalarText(connection, "PRAGMA journal_mode;"));
            Assert.Equal(1L, ScalarLong(connection, "PRAGMA synchronous;"));   // NORMAL
            Assert.Equal(5000L, ScalarLong(connection, "PRAGMA busy_timeout;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Apply_SecondCall_KeepsConnectionLevelPragmas()
    {
        var path = TempPath();
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();

            SqlitePragmas.Apply(connection);
            SqlitePragmas.Apply(connection);   // WAL 已缓存，仍须重设连接级 PRAGMA

            Assert.Equal("wal", ScalarText(connection, "PRAGMA journal_mode;"));
            Assert.Equal(1L, ScalarLong(connection, "PRAGMA synchronous;"));
            Assert.Equal(5000L, ScalarLong(connection, "PRAGMA busy_timeout;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
