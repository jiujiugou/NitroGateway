using System.Reflection;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Persistence;

/// <summary>FluentMigrator 迁移执行器，启动时调用一次</summary>
public static class MigrationRunner
{
    /// <summary>
    /// 执行所有待运行迁移。
    /// 策略：
    /// 1. 迁移前备份 SQLite 文件（最多保留 5 份）
    /// 2. 执行幂等迁移（FluentMigrator 自动跳过已执行过的）
    /// 3. 更新 app_meta 中的版本号
    /// </summary>
    public static void Run(string connectionString, ILogger? logger = null)
    {
        // ── 1. 预迁移备份 ──
        var dbPath = ExtractDataSource(connectionString);
        BackupDatabase(dbPath, logger);

        // ── 2. 建临时连接（FluentMigrator 内部自己管理连接） ──
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();

        // ── 3. 记录当前版本 ──
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        RecordVersion(connection, appVersion, logger);
    }

    // ═══════ 备份 ═══════

    private static void BackupDatabase(string dbPath, ILogger? logger)
    {
        if (!File.Exists(dbPath)) return;

        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "backups");
        Directory.CreateDirectory(backupDir);

        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var backupPath = Path.Combine(backupDir, $"nitrogateway.{timestamp}.bak");
        File.Copy(dbPath, backupPath, overwrite: true);
        logger?.LogInformation("数据库已备份: {BackupPath}", backupPath);

        // 只保留最近 5 份
        var backups = Directory.GetFiles(backupDir, "nitrogateway.*.bak")
            .OrderByDescending(f => f)
            .ToList();
        foreach (var old in backups.Skip(5))
        {
            File.Delete(old);
            logger?.LogDebug("清理旧备份: {Old}", old);
        }
    }

    // ═══════ 版本记录 ═══════

    private static void RecordVersion(SqliteConnection connection, string version, ILogger? logger)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO app_meta (key, value, updated_at)
                VALUES ('app_version', @v, @ts)
                ON CONFLICT(key) DO UPDATE SET value=@v, updated_at=@ts";
            cmd.Parameters.AddWithValue("@v", version);
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            logger?.LogInformation("应用版本已记录: {Version}", version);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            // M006 还没执行，表不存在，跳过
            logger?.LogDebug("app_meta 表尚未创建，跳过版本记录");
        }
    }

    // ═══════ 工具 ═══════

    /// <summary>从连接串中提取文件路径。格式: "Data Source=/path/to/db"</summary>
    private static string ExtractDataSource(string connectionString)
    {
        foreach (var part in connectionString.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                return trimmed["Data Source=".Length..].Trim();
        }
        throw new InvalidOperationException($"无法从连接串提取 Data Source: {connectionString}");
    }
}
