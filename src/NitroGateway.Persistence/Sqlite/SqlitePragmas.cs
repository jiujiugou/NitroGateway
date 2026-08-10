using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 连接 PRAGMA 统一应用入口（ADR-002 P2-1 / ADR-018 P3-3）。
/// 默认 rollback journal 模式读写互斥，与 1s 采集写 + 前端查询 + Alarm 并发不匹配；
/// WAL 允许读写并行，synchronous=NORMAL 兼顾持久性与写入性能，
/// busy_timeout 避免并发写锁冲突时立即报 "database is locked"。
/// 供同程序集内各存储与外部宿主（Ingest 中心库）共用，保证库级设置一致。
/// </summary>
public static class SqlitePragmas
{
    /// <summary>
    /// 已确认 WAL 模式的数据库文件路径集合（<see cref="SqliteConnection.DataSource"/>）。
    /// journal_mode=WAL 是库级持久设置（写入库文件头），只需成功设置一次，
    /// 之后所有连接跳过该往返（ADR-018 P3-3）。
    /// </summary>
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
        // ADR-018 P3-3：WAL 只在每个库文件首次打开时设置一次，省掉热路径每操作一次往返
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
