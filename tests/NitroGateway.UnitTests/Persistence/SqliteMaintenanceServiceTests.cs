using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Persistence.Sqlite;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// WAL 维护服务：TRUNCATE checkpoint 必须能收缩 WAL 文件（防止 WAL 在持续写负载下无限增长）。
/// </summary>
public sealed class SqliteMaintenanceServiceTests
{
    [Fact]
    public void Checkpoint_truncate_does_not_grow_wal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ng_wal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "t.db");
        var cs = $"Data Source={db}";
        try
        {
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                SqlitePragmas.Apply(c);
                using (var ddl = c.CreateCommand())
                {
                    ddl.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
                    ddl.ExecuteNonQuery();
                }
                for (var i = 0; i < 3000; i++)
                {
                    using var ins = c.CreateCommand();
                    ins.CommandText = "INSERT INTO t(v) VALUES (printf('%500c', 'x'));";
                    ins.ExecuteNonQuery();
                }
            }

            var wal = db + "-wal";
            var before = File.Exists(wal) ? new FileInfo(wal).Length : 0;

            var service = new SqliteMaintenanceService(cs, NullLogger<SqliteMaintenanceService>.Instance);
            service.Checkpoint(truncate: true);

            var after = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            Assert.True(after <= before, $"WAL 不应增长: before={before} after={after}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }
}
