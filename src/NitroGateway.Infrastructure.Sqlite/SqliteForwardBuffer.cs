using System.Text.Json;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Infrastructure.Sqlite;

/// <summary>SQLite 转发缓冲实现。FIFO 队列，两阶段提交</summary>
public sealed class SqliteForwardBuffer : IForwardBuffer, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Count
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public SqliteForwardBuffer(SqliteConnection connection)
    {
        _connection = connection;
        _connection.Open();
    }

    public async Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(batch, _jsonOptions);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO forward_buffer (id, payload, status, enqueued_at)
            VALUES (@id, @payload, 'Pending', @ts)";
        cmd.Parameters.AddWithValue("@id", batch.Id.ToString());
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        return OperationResult.Success();
    }

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
            return OperationalError.General($"Buffer 提交失败: {ex.Message}");
        }
    }

    public void Dispose() => _connection.Dispose();
}
