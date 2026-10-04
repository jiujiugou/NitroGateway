using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite WAL 维护服务：定期 checkpoint，防止 WAL 无限增长。
/// <para>
/// 背景：WAL 模式下自动 checkpoint 在持续写负载/短连接频繁打开时会被饿死，
/// WAL 文件可增长到数 GB，反过来拖慢所有读写（实测出队 4.5s、入队 134ms）。
/// 本服务每轮执行 PASSIVE checkpoint（不阻塞读写，推进已提交页合并回主库），
/// 每 <see cref="TruncateEveryCycles"/> 轮执行一次 TRUNCATE（收缩 WAL 文件）。
/// </para>
/// 单轮失败只记日志，不影响宿主其余功能；停机时随 BasedService 取消退出。
/// </summary>
public sealed class SqliteMaintenanceService : BackgroundService
{
    /// <summary>每多少轮 PASSIVE 后执行一次 TRUNCATE（收缩文件大小）。</summary>
    private const int TruncateEveryCycles = 10;

    private readonly string _connectionString;
    private readonly ILogger<SqliteMaintenanceService> _logger;
    private readonly TimeSpan _interval;
    private int _cycle;

    /// <param name="connectionString">SQLite 连接串（Persistence:ConnectionString）</param>
    /// <param name="logger">日志</param>
    /// <param name="interval">维护间隔，默认 60 秒（测试可注入小间隔）</param>
    public SqliteMaintenanceService(
        string connectionString,
        ILogger<SqliteMaintenanceService> logger,
        TimeSpan? interval = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一个周期，避免与启动期迁移/恢复争抢
        try { await Task.Delay(_interval, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var truncate = Interlocked.Increment(ref _cycle) % TruncateEveryCycles == 0;
                Checkpoint(truncate);
            }
            catch (Exception ex)
            {
                // 被其他连接占用（busy）时跳过本轮，下轮再试
                _logger.LogDebug(ex, "SQLite WAL checkpoint 失败，下轮重试");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>执行一次 WAL checkpoint（内部，供测试直接调用）。</summary>
    internal void Checkpoint(bool truncate)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqlitePragmas.Apply(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = truncate
            ? "PRAGMA wal_checkpoint(TRUNCATE);"
            : "PRAGMA wal_checkpoint(PASSIVE);";
        cmd.ExecuteScalar();
    }
}
