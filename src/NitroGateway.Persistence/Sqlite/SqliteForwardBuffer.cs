using System.Text.Json;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 转发缓冲实现。FIFO 队列，两阶段提交，带死信队列。
/// 转发失败的消息累加重试计数，达到上限后自动移入 DeadLetter 状态。
/// </summary>
public sealed class SqliteForwardBuffer : IForwardBuffer, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _maxRetries;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>当前 Pending 批次数（不含死信）</summary>
    public int Count
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// 创建 SQLite 转发缓冲。
    /// </summary>
    /// <param name="connection">已配置但未打开的 SQLite 连接。构造函数内 Open</param>
    /// <param name="maxRetries">最大重试次数，超过后移入死信队列。默认 5</param>
    public SqliteForwardBuffer(SqliteConnection connection, int maxRetries = 5)
    {
        _connection = connection;
        _maxRetries = maxRetries;
        _connection.Open();
    }

    // ═══════════════════════════════════════════════════════════════
    //  入队 / 出队 / 提交
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(batch, _jsonOptions);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO forward_buffer (id, payload, status, retry_count, enqueued_at)
            VALUES (@id, @payload, 'Pending', 0, @ts)";
        cmd.Parameters.AddWithValue("@id", batch.Id.ToString());
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
        int maxCount, CancellationToken ct = default)
    {
        var results = new List<BatchMeasurements>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, payload FROM forward_buffer
            WHERE status = 'Pending'
            ORDER BY enqueued_at ASC
            LIMIT @max";
        cmd.Parameters.AddWithValue("@max", maxCount);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.GetString(1);
            var batch = JsonSerializer.Deserialize<BatchMeasurements>(payload, _jsonOptions);
            if (batch is not null) results.Add(batch);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        if (batchIds.Count == 0) return OperationResult.Success();

        await using var tx = await _connection.BeginTransactionAsync(ct);

        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;

            foreach (var id in batchIds)
            {
                cmd.CommandText = "DELETE FROM forward_buffer WHERE id = @id";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", id.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return SqliteErrorClassifier.Classify(ex, "Buffer 提交失败");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  死信队列
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<OperationResult> MarkFailedAsync(
        Guid batchId, string reason, CancellationToken ct = default)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE forward_buffer
                SET retry_count = retry_count + 1,
                    last_error = @error
                WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", batchId.ToString());
            cmd.Parameters.AddWithValue("@error", reason);
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = @"
                UPDATE forward_buffer
                SET status = 'DeadLetter'
                WHERE id = @id AND retry_count >= @maxRetries";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@id", batchId.ToString());
            cmd.Parameters.AddWithValue("@maxRetries", _maxRetries);
            await cmd.ExecuteNonQueryAsync(ct);

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "标记失败异常");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(
        int maxCount, CancellationToken ct = default)
    {
        var results = new List<DeadLetterEntry>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT id, payload, retry_count, last_error, enqueued_at
            FROM forward_buffer
            WHERE status = 'DeadLetter'
            ORDER BY enqueued_at ASC
            LIMIT @max";
        cmd.Parameters.AddWithValue("@max", maxCount);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var batchId = Guid.Parse(reader.GetString(0));
            var payload = reader.GetString(1);
            var retryCount = reader.GetInt32(2);
            var lastError = reader.IsDBNull(3) ? null : reader.GetString(3);
            var enqueuedAt = DateTime.Parse(reader.GetString(4));

            var batch = JsonSerializer.Deserialize<BatchMeasurements>(payload, _jsonOptions);

            results.Add(new DeadLetterEntry
            {
                BatchId = batchId,
                DeviceId = batch?.DeviceId ?? Guid.Empty,
                RecordCount = batch?.Records.Count ?? 0,
                RetryCount = retryCount,
                LastError = lastError,
                EnqueuedAt = enqueuedAt
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE forward_buffer
                SET status = 'Pending',
                    retry_count = 0,
                    last_error = NULL
                WHERE id = @id AND status = 'DeadLetter'";
            cmd.Parameters.AddWithValue("@id", batchId.ToString());
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在或已不是 DeadLetter 状态");
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "重放死信异常");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM forward_buffer WHERE id = @id AND status = 'DeadLetter'";
            cmd.Parameters.AddWithValue("@id", batchId.ToString());
            var rows = await cmd.ExecuteNonQueryAsync(ct);

            return rows > 0
                ? OperationResult.Success()
                : OperationalError.NotFound($"死信 {batchId} 不存在或已不是 DeadLetter 状态");
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "丢弃死信异常");
        }
    }

    public void Dispose() => _connection.Dispose();
}
