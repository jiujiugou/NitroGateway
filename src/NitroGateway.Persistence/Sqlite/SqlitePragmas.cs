using Microsoft.Data.Sqlite;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 连接 PRAGMA 统一应用入口（ADR-002 P2-1）。
/// 默认 rollback journal 模式读写互斥，与 1s 采集写 + 前端查询 + Alarm 并发不匹配；
/// WAL 允许读写并行，synchronous=NORMAL 兼顾持久性与写入性能，
/// busy_timeout 避免并发写锁冲突时立即报 "database is locked"。
/// </summary>
internal static class SqlitePragmas
{
    /// <summary>
    /// 对已打开的连接应用 PRAGMA。
    /// 必须在事务外调用（WAL 模式切换不允许在事务内）。
    /// journal_mode=WAL 是库级持久设置，synchronous/busy_timeout 为连接级。
    /// </summary>
    public static void Apply(SqliteConnection connection)
    {
        // 逐条执行：Microsoft.Data.Sqlite 对多语句批处理支持不可靠
        foreach (var pragma in new[] { "PRAGMA journal_mode=WAL;", "PRAGMA synchronous=NORMAL;", "PRAGMA busy_timeout=5000;" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }
}
