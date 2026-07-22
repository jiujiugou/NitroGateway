using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 转发缓冲实现。FIFO 队列，两阶段提交，带死信队列。
/// </summary>
public sealed class SqliteForwardBuffer : IForwardBuffer, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _maxRetries;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Count =>
        _connection.ExecuteScalar<int>("SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'");

    public SqliteForwardBuffer(SqliteConnection connection, int maxRetries = 5)
    {
        _connection = connection;
        _maxRetries = maxRetries;
        _connection.Open();
    }

    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(batch, _json);
        await _connection.ExecuteAsync(
            "INSERT INTO forward_buffer (id, payload, status, retry_count, enqueued_at) VALUES (@id, @payload, 'Pending', 0, @ts)",
            new { id = batch.Id.ToString(), payload, ts = DateTime.UtcNow.ToString("O") });
        return OperationResult.Success();
    }

    public async Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
    int maxCount,
    CancellationToken ct = default)
    {
        await using var tx = await _connection.BeginTransactionAsync(ct);

        try
        {
            // ① 查询待发送的数据
            var rows = (await _connection.QueryAsync<BufferRow>(
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

            // ② 标记为 InFlight
            await _connection.ExecuteAsync(
                new CommandDefinition( @"UPDATE forward_buffer SET status = 'InFlight' 
                    WHERE id IN @ids",
                    new
                    {
                        ids = rows.Select(r => r.Id)
                    },
                    transaction: tx,
                    cancellationToken: ct));

            await tx.CommitAsync(ct);

            // ③ 反序列化
            var result = rows
                .Select(r => JsonSerializer.Deserialize<BatchMeasurements>(r.Payload, _json))
                .Where(b => b is not null)
                .Cast<BatchMeasurements>()
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return SqliteErrorClassifier.Classify(ex, "Buffer 出队失败");
        }
    }

    public async Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        if (batchIds.Count == 0) return OperationResult.Success();

        await using var tx = await _connection.BeginTransactionAsync(ct);
        try
        {
            await _connection.ExecuteAsync(
                "DELETE FROM forward_buffer WHERE id IN @ids",
                new { ids = batchIds.Select(id => id.ToString()) }, tx);
            await tx.CommitAsync(ct);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
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
            // 失败以后恢复 Pending
            await _connection.ExecuteAsync(
                @"UPDATE forward_buffer
              SET
                    status = 'Pending',
                    retry_count = retry_count + 1,
                    last_error = @error
              WHERE id = @id",
                new
                {
                    id = batchId.ToString(),
                    error = reason
                });

            // 超过重试次数进入死信
            await _connection.ExecuteAsync(
                @"UPDATE forward_buffer
              SET status = 'DeadLetter'
              WHERE id = @id
                AND retry_count >= @max",
                new
                {
                    id = batchId.ToString(),
                    max = _maxRetries
                });

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "标记失败异常");
        }
    }

    public async Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
    {
        var rows = await _connection.QueryAsync(
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
        var rows = await _connection.ExecuteAsync(
            "UPDATE forward_buffer SET status = 'Pending', retry_count = 0, last_error = NULL WHERE id = @id AND status = 'DeadLetter'",
            new { id = batchId.ToString() });

        return rows > 0
            ? OperationResult.Success()
            : OperationalError.NotFound($"死信 {batchId} 不存在");
    }

    public async Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
    {
        var rows = await _connection.ExecuteAsync(
            "DELETE FROM forward_buffer WHERE id = @id AND status = 'DeadLetter'",
            new { id = batchId.ToString() });

        return rows > 0
            ? OperationResult.Success()
            : OperationalError.NotFound($"死信 {batchId} 不存在");
    }

    public void Dispose() => _connection.Dispose();
}
