using System;
using System.IO;
using NitroGateway.Persistence;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

public class MigrationRunnerTests
{
    [Theory]
    [InlineData("Data Source=/data/ntg.db", "/data/ntg.db")]
    [InlineData("Data Source = /data/ntg.db", "/data/ntg.db")]
    [InlineData("data source=/data/ntg.db;Cache=Shared", "/data/ntg.db")]
    [InlineData("Mode=ReadWrite;Data Source=C:\\data\\ntg.db", "C:\\data\\ntg.db")]
    public void ExtractDataSource_ParsesVariants(string connectionString, string expected)
    {
        var actual = MigrationRunner.ExtractDataSource(connectionString);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 没有待执行迁移时不得做全量备份：否则每次启动都复制整个主库（数 GB），
    /// 同步阻塞宿主与后台服务启动。第二次 Run（schema 已最新）不应新增 .bak。
    /// </summary>
    [Fact]
    public void Run_WhenNoPendingMigrations_DoesNotCreateBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ng-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "test.db")}";
        try
        {
            // 首次：库不存在 → 不备份，仅建 schema + 记录版本
            MigrationRunner.Run(connectionString);

            var backupsDir = Path.Combine(dir, "backups");
            var before = Directory.Exists(backupsDir) ? Directory.GetFiles(backupsDir, "*.bak").Length : 0;

            // 再次：无待执行迁移 → 不得触发全量备份
            MigrationRunner.Run(connectionString);

            var after = Directory.Exists(backupsDir) ? Directory.GetFiles(backupsDir, "*.bak").Length : 0;
            Assert.Equal(0, before);
            Assert.Equal(before, after);
        }
        finally
        {
            // Microsoft.Data.Sqlite 会池化连接并持有文件句柄，先清池再删目录
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
