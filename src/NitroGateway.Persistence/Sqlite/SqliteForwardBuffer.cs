using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 转发缓冲实现。FIFO 队列，两阶段提交，带死信队列。
/// ADR-001 P1-4：每个操作使用独立 SqliteConnection（参考 SqliteMeasurementStore 模式），
/// 不再共享 Singleton 连接，避免 Collection/Forwarder/Alarm 跨线程并发使用同一连接。
/// </summary>
public sealed class SqliteForwardBuffer : IForwardBuffer
{
    private readonly string _connectionString;
    private readonly int _maxRetries;
    private readonly ILogger<SqliteForwardBuffer> _logger;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Count
    {
        get
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'");
        }
    }

    /// <param name="maxRetries">最大重试次数，超过后移入死信队列。默认 5</param>
    public SqliteForwardBuffer(string connectionString, ILogger<SqliteForwardBuffer> logger, int maxRetries = 5)
    {
        _connectionString = connectionString;
        _logger = logger;
        _maxRetries = maxRetries;

        // P0-1① 启动恢复：进程崩溃/重启后，把遗留 InFlight 批次全部重置为 Pending，
        // 避免批次永久卡在 InFlight（不计 Count、不再出队、不进死信）造成静默丢数。
        // 恢复失败仅告警，不阻断网关启动（下次启动仍会重试恢复）。
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var recovered = conn.Execute(
                "UPDATE forward_buffer SET status = 'Pending' WHERE status = 'InFlight'");
            if (recovered > 0)
            {
                _logger.LogWarning(
                    "启动恢复：{Count} 个 InFlight 转发批次已重置为 Pending（上次进程可能异常退出）",
                    recovered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动恢复 InFlight 批次失败");
        }
    }

    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        // P0-2：入队异常统一走 SqliteErrorClassifier，与 Dequeue/Commit/MarkFailed 一致，
        // 使 DataDispatcher 的优雅降级分支（bufResult.IsFailure）真正可达。
        try
        {
            var payload = JsonSerializer.Serialize(batch, _json);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(
                "INSERT INTO forward_buffer (id, payload, status, retry_count, enqueued_at) VALUES (@id, @payload, 'Pending', 0, @ts)",
                new { id = batch.Id.ToString(), payload, ts = DateTime.UtcNow.ToString("O") });
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 入队失败");
        }
    }

    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
    int maxCount,
    CancellationToken ct = default)
    {
        List<BufferRow> rows;
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            // ① 查询待发送的数据并标记为 InFlight（同一事务两阶段提交）
            await using var tx = await conn.BeginTransactionAsync(ct);

            rows = (await conn.QueryAsync<BufferRow>(
                new CommandDefinition(@"SELECT id, payload FROM forward_buffer WHERE status = 'Pending'
                  ORDER BY enqueued_at ASC LIMIT @max",
                    new { max = maxCount },
                    transaction: tx,
                    cancellationToken: ct)))
                .ToList();

            if (rows.Count == 0)
            {
                await tx.CommitAsync(ct);
                return Array.Empty<BatchMeasurements>();
            }

            await conn.ExecuteAsync(
                new CommandDefinition( @"UPDATE forward_buffer SET status = 'InFlight' 
                    WHERE id IN @ids",
                    new
                    {
                        ids = rows.Select(r => r.Id)
                    },
                    transaction: tx,
                    cancellationToken: ct));

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // 事务未提交时在作用域结束时自动回滚
            return SqliteErrorClassifier.Classify(ex, "Buffer 出队失败");
        }

        // ② 反序列化。损坏行不能卡在 InFlight（P0-1②）：
        //    重置为 Pending + retry_count+1 + last_error，超过 _maxRetries 进死信。
        //    事务已随作用域释放，恢复逻辑可复用 MarkFailedAsync 开启新事务。
        var result = new List<BatchMeasurements>(rows.Count);
        foreach (var row in rows)
        {
            BatchMeasurements? batch;
            try
            {
                batch = JsonSerializer.Deserialize<BatchMeasurements>(row.Payload, _json);
            }
            catch (Exception ex)
            {
                await RecoverCorruptRowAsync(row.Id, $"反序列化失败: {ex.Message}", ct);
                continue;
            }

            if (batch is null)
            {
                await RecoverCorruptRowAsync(row.Id, "反序列化结果为 null（负载损坏）", ct);
                continue;
            }

            result.Add(batch);
        }

        return result;
    }

    /// <summary>
    /// P0-1② 出队反序列化失败恢复：复用 <see cref="MarkFailedAsync"/> 的重试/死信逻辑，
    /// 恢复自身失败仅记日志，不影响其余行出队。
    /// </summary>
    private async Task RecoverCorruptRowAsync(string id, string reason, CancellationToken ct)
    {
        try
        {
            var result = await MarkFailedAsync(Guid.Parse(id), reason, ct);
            if (result.IsFailure)
            {
                _logger.LogError("恢复损坏批次 {BatchId} 失败: {Error}", id, result.Error!.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复损坏批次 {BatchId} 异常", id);
        }
    }

    public async Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        if (batchIds.Count == 0) return OperationResult.Success();

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(
                "DELETE FROM forward_buffer WHERE id IN @ids",
                new { ids = batchIds.Select(id => id.ToString()) }, tx);
            await tx.CommitAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "Buffer 提交失败");
        }
    }

    public async Task<OperationResult> MarkFailedAsync(
    Guid batchId,
    string reason,
    CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            // 恢复 Pending + 记录重试次数
            await conn.ExecuteAsync(
                @"UPDATE forward_buffer
              SET
                    status = 'Pending',
                    retry_count = retry_count + 1,
                    last_error = @error
              WHERE id = @id",
                new { id = batchId.ToString(), error = reason }, tx);

            // 超过重试次数进入死信
            await conn.ExecuteAsync(
                @"UPDATE forward_buffer
              SET status = 'DeadLetter'
              WHERE id = @id
                AND retry_count >= @max",
                new { id = batchId.ToString(), max = _maxRetries }, tx);

            await tx.CommitAsync(ct);

            // 如果进入死信，记录 Warning
            var deadCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM forward_buffer WHERE id=@id AND status='DeadLetter'",
                new { id = batchId.ToString() });
            if (deadCount > 0)
            {
                _logger.LogWarning(
                    "转发批次 {BatchId} 进入死信队列（重试 {MaxRetries} 次后失败）: {Error}",
                    batchId, _maxRetries, reason);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "标记失败异常");
        }
    }

    public async Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync(
            "SELECT id, payload, retry_count, last_error, enqueued_at FROM forward_buffer WHERE status = 'DeadLetter' ORDER BY enqueued_at ASC LIMIT @max",
            new { max = maxCount });

        return rows.Select(r =>
        {
            var batch = JsonSerializer.Deserialize<BatchMeasurements>((string)r.payload, _json);
            return new DeadLetterEntry
            {
                BatchId = Guid.Parse((string)r.id),
                DeviceId = batch?.DeviceId ?? Guid.Empty,
                RecordCount = batch?.Records.Count ?? 0,
                RetryCount = (int)r.retry_count,
                LastError = r.last_error as string,
                EnqueuedAt = DateTime.Parse((string)r.enqueued_at)
            };
        }).ToList();
    }

    public async Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            "UPDATE forward_buffer SET status = 'Pending', retry_count = 0, last_error = NULL WHERE id = @id AND status = 'DeadLetter'",
            new { id = batchId.ToString() });

        return rows > 0
            ? OperationResult.Success()
            : OperationalError.NotFound($"死信 {batchId} 不存在");
    }

    public async Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM forward_buffer WHERE id = @id AND status = 'DeadLetter'",
            new { id = batchId.ToString() });

        return rows > 0
            ? OperationResult.Success()
            : OperationalError.NotFound($"死信 {batchId} 不存在");
    }

}
