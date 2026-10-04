using System.Reflection;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NitroGateway.Persistence.Sqlite;

namespace NitroGateway.Persistence;

/// <summary>
/// FluentMigrator 迁移执行器，应用启动时由 <see cref=\ DatabaseInitializationExtensions.InitializeDatabase\/> 调用一次。
/// 负责：迁移前文件备份、幂等执行全部待运行迁移、记录应用版本到 app_meta。
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// 执行所有待运行迁移。
    /// 策略：
    /// 1. 迁移前备份 SQLite 文件（最多保留 5 份）
    /// 2. 执行幂等迁移（FluentMigrator 自动跳过已执行过的）
    /// 3. 更新 app_meta 中的版本号
    /// </summary>
    /// <param name=\connectionString\>SQLite 连接串（须含 Data Source）</param>
    /// <param name=\logger\>迁移/备份日志输出，可空（不传则静默）</param>
    public static void Run(string connectionString, ILogger? logger = null)
    {
        // ── 1. 建临时连接（FluentMigrator 内部自己管理连接；此处用于 PRAGMA 与备份） ──
        var dbPath = ExtractDataSource(connectionString);
        var dbExistsBeforeOpen = File.Exists(dbPath);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        SqlitePragmas.Apply(connection);

        // 启动期 WAL checkpoint：WAL 异常增长（数 GB）会拖慢全部读写，启动时先回收一次。
        // WAL 较大时用 TRUNCATE（阻塞但一次性），较小用 PASSIVE（快且不阻塞）。失败不阻断启动。
        CheckpointOnStartup(connection, dbPath, logger);

        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        // ── 2. 预迁移备份（仅当确有迁移待执行；库已存在；WAL 下先 checkpoint 再复制，保证一致性） ──
        // 关键：无待执行迁移时绝不做全量复制——否则每次启动都要复制数 GB 主库，
        // 同步阻塞宿主与全部后台服务启动（本机实测 4GB 库复制约 100s）。
        if (dbExistsBeforeOpen && runner.HasMigrationsToApplyUp())
            BackupDatabase(connection, dbPath, logger);

        runner.MigrateUp();

        // ── 3. 记录当前版本 ──
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        RecordVersion(connection, appVersion, logger);
    }

    /// <summary>
    /// 启动期 WAL checkpoint。WAL 超过 256MB 用 TRUNCATE（回收文件），否则 PASSIVE（快速）。
    /// 失败仅记日志不阻断启动（运行期由 <see cref="Sqlite.SqliteMaintenanceService"/> 兜底）。
    /// </summary>
    private static void CheckpointOnStartup(SqliteConnection connection, string dbPath, ILogger? logger)
    {
        try
        {
            var walPath = dbPath + "-wal";
            var walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
            var truncate = walBytes > 256L * 1024 * 1024;

            using var command = connection.CreateCommand();
            command.CommandText = truncate
                ? "PRAGMA wal_checkpoint(TRUNCATE);"
                : "PRAGMA wal_checkpoint(PASSIVE);";
            command.ExecuteScalar();

            if (walBytes > 0)
            {
                logger?.LogInformation("启动 WAL checkpoint（{Mode}）：checkpoint 前 WAL {WalMB:F1}MB",
                    truncate ? "TRUNCATE" : "PASSIVE", walBytes / (1024.0 * 1024.0));
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "启动 WAL checkpoint 失败，忽略（运行期维护服务会重试）");
        }
    }

    /// <summary>
    /// 预迁移备份：将主库文件复制到 backups 目录（文件名带时间戳），并清理只保留最近 5 份。
    /// WAL 模式下先执行 <c>PRAGMA wal_checkpoint(TRUNCATE)</c> 把已提交数据合并回主库文件再复制，
    /// 保证备份不缺失最近已提交数据、也不拿到不一致快照。
    /// 备份失败会让启动直接失败（迁移前必须有可回退现场）。
    /// </summary>
    private static void BackupDatabase(SqliteConnection connection, string dbPath, ILogger? logger)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "backups");
        Directory.CreateDirectory(backupDir);

        // 再复制，避免备份缺最近已提交数据或拿到不一致快照
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>
    /// 将当前程序集版本（x.y.z）写入 app_meta（key='app_version'），供运维与诊断查询。
    /// 使用 UPSERT 语义：已存在则覆盖 value 与 updated_at。
    /// 若 M006 迁移尚未执行（表不存在），视为可跳过场景仅记 Debug 日志，不阻断启动。
    /// </summary>
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

    internal static string ExtractDataSource(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString).DataSource;
}
