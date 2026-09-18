using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace NitroGateway.Persistence.Sqlite;

public static class SqlitePragmas
{
    private static readonly ConcurrentDictionary<string, byte> WalConfirmed = new();

    /// <summary>
    /// 对已打开的连接应用 PRAGMA。
    /// 必须在事务外调用（WAL 模式切换不允许在事务内）。
    /// journal_mode=WAL 为库级持久设置，首次打开后缓存跳过；
    /// synchronous/busy_timeout 为连接级，每次打开都要执行（Microsoft.Data.Sqlite
    /// 单命令不支持多语句，保持逐条执行，热路径每操作 2 次往返）。
    /// </summary>
    public static void Apply(SqliteConnection connection)
    {
        if (!WalConfirmed.ContainsKey(connection.DataSource))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL;";
                command.ExecuteNonQuery();
            }
            WalConfirmed.TryAdd(connection.DataSource, 0);
        }

        // 连接级 PRAGMA：每次打开都需要
        foreach (var pragma in new[] { "PRAGMA synchronous=NORMAL;", "PRAGMA busy_timeout=5000;" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }
}
