using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteForwardBuffer 数据可靠性测试（ADR-001 P0-1/P0-2 + P1-6 补充）。
/// ADR-001 P1-4 后每个操作使用独立连接，因此用临时文件库（而非共享 :memory: 连接）承载测试。
/// 覆盖：InFlight 启动恢复、损坏负载恢复、入队异常分类、正常往返、
/// MarkFailed 超限死信、死信查询/重试/丢弃、Commit 删除。
/// </summary>
public class SqliteForwardBufferTests
{
    /// <summary>临时文件库：建表并在释放时删除文件；Pooling=False 避免文件句柄占用。</summary>
    private sealed class TempForwardBufferDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempForwardBufferDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-fwd-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE forward_buffer (
                    id TEXT PRIMARY KEY,
                    payload TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'Pending',
                    enqueued_at TEXT NOT NULL,
                    retry_count INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void InsertRow(
        string connectionString, string id, string payload, string status, int retryCount = 0)
    {
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO forward_buffer (id, payload, status, enqueued_at, retry_count, last_error)
            VALUES (@id, @payload, @status, @ts, @retry, NULL);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@ts", "2026-08-06T00:00:00Z");
        command.Parameters.AddWithValue("@retry", retryCount);
        command.ExecuteNonQuery();
    }

    private static (string Status, int RetryCount, string? LastError) ReadRow(
        string connectionString, string id)
    {
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, retry_count, last_error FROM forward_buffer WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"forward_buffer 中不存在 id={id}");
        var status = reader.GetString(0);
        var retryCount = reader.GetInt32(1);
        var lastError = reader.IsDBNull(2) ? null : reader.GetString(2);
        return (status, retryCount, lastError);
    }

    private static void DropForwardBufferTable(string connectionString)
    {
        using var connection = Open(connectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE forward_buffer";
        command.ExecuteNonQuery();
    }

    private static BatchMeasurements NewBatch(Guid id)
    {
        return new BatchMeasurements
        {
            Id = id,
            DeviceId = Guid.NewGuid(),
            ScanStartedAt = DateTime.UtcNow.AddSeconds(-1),
            ScanCompletedAt = DateTime.UtcNow,
            Records =
            [
                new MeasurementRecord
                {
                    Id = Guid.NewGuid(),
                    DeviceId = Guid.NewGuid(),
                    DevicePointId = Guid.NewGuid(),
                    PointName = "T1",
                    Value = 36.6d,
                    DataType = DataType.Float,
                    Timestamp = DateTime.UtcNow,
                    ReceivedAt = DateTime.UtcNow,
                    Quality = QualityCode.Good
                }
            ]
        };
    }

    private static string Serialize(BatchMeasurements batch) =>
        JsonSerializer.Serialize(batch, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>P0-1①：构造时把遗留 InFlight 重置为 Pending，进程崩溃后批次可继续出队</summary>
    [Fact]
    public async Task Constructor_ResetsStaleInFlight_ToPending()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "{}", "InFlight");
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        Assert.Equal(1, buffer.Count);

        var result = await buffer.DequeueAsync(10);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal("InFlight", ReadRow(db.ConnectionString, batchId).Status);
    }

    /// <summary>P0-1②：损坏负载出队后恢复为 Pending + 重试计数 + 错误记录，不再卡 InFlight</summary>
    [Fact]
    public async Task Dequeue_CorruptPayload_ResetsToPendingWithRetryCount()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "{not-json", "Pending");
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance, maxRetries: 3);

        var result = await buffer.DequeueAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Equal(1, buffer.Count);

        var (status, retryCount, lastError) = ReadRow(db.ConnectionString, batchId);
        Assert.Equal("Pending", status);
        Assert.Equal(1, retryCount);
        Assert.Contains("反序列化", lastError);
    }

    /// <summary>P0-1②：损坏负载重试超限后进入死信队列</summary>
    [Fact]
    public async Task Dequeue_CorruptPayload_OverMaxRetries_MovesToDeadLetter()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "{not-json", "Pending", retryCount: 2);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance, maxRetries: 2);

        var result = await buffer.DequeueAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
        Assert.Equal(0, buffer.Count);

        var (status, retryCount, lastError) = ReadRow(db.ConnectionString, batchId);
        Assert.Equal("DeadLetter", status);
        Assert.Equal(3, retryCount);
        Assert.Contains("反序列化", lastError);
    }

    /// <summary>P0-1②：负载为 null 同样视为损坏，恢复为 Pending + 重试计数</summary>
    [Fact]
    public async Task Dequeue_NullPayload_ResetsToPendingWithRetryCount()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "null", "Pending");
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance, maxRetries: 3);

        var result = await buffer.DequeueAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);

        var (status, retryCount, lastError) = ReadRow(db.ConnectionString, batchId);
        Assert.Equal("Pending", status);
        Assert.Equal(1, retryCount);
        Assert.Contains("null", lastError);
    }

    /// <summary>P0-2：入队异常（主键冲突）包装为 OperationResult 失败，不再直接抛出</summary>
    [Fact]
    public async Task Enqueue_DuplicateBatchId_ReturnsClassifiedFailure()
    {
        using var db = new TempForwardBufferDb();
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var batch = NewBatch(Guid.NewGuid());
        Assert.True((await buffer.EnqueueAsync(batch)).IsSuccess);

        var result = await buffer.EnqueueAsync(batch);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("入队失败", result.Error.Message);
    }

    /// <summary>正常入队 → 出队往返，验证主流程未被破坏</summary>
    [Fact]
    public async Task Enqueue_ThenDequeue_Roundtrip()
    {
        using var db = new TempForwardBufferDb();
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var batch = NewBatch(Guid.NewGuid());
        Assert.True((await buffer.EnqueueAsync(batch)).IsSuccess);
        Assert.Equal(1, buffer.Count);

        var result = await buffer.DequeueAsync(10);

        Assert.True(result.IsSuccess);
        var dequeued = Assert.Single(result.Value!);
        Assert.Equal(batch.Id, dequeued.Id);
        Assert.Equal(batch.Records[0].PointName, dequeued.Records[0].PointName);
    }

    /// <summary>P1-6：MarkFailed 重试超限 → 正常批次进入死信队列（Forwarder 发布失败路径）</summary>
    [Fact]
    public async Task MarkFailed_OverMaxRetries_MovesToDeadLetter()
    {
        using var db = new TempForwardBufferDb();
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance, maxRetries: 2);
        var batch = NewBatch(Guid.NewGuid());

        await buffer.EnqueueAsync(batch);
        await buffer.MarkFailedAsync(batch.Id, "broker 不可达");

        var (afterFirst, retryAfterFirst, _) = ReadRow(db.ConnectionString, batch.Id.ToString());
        Assert.Equal("Pending", afterFirst);
        Assert.Equal(1, retryAfterFirst);

        await buffer.MarkFailedAsync(batch.Id, "broker 不可达");

        var (status, retryCount, lastError) = ReadRow(db.ConnectionString, batch.Id.ToString());
        Assert.Equal("DeadLetter", status);
        Assert.Equal(2, retryCount);
        Assert.Contains("broker", lastError);
    }

    /// <summary>ADR-001 P3-13：GetCountAsync 异步返回 Pending 批次数，不含死信</summary>
    [Fact]
    public async Task GetCountAsync_ReturnsPendingCount_ExcludesDeadLetters()
    {
        using var db = new TempForwardBufferDb();
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);
        await buffer.EnqueueAsync(NewBatch(Guid.NewGuid()));
        InsertRow(db.ConnectionString, Guid.NewGuid().ToString(), "{}", "DeadLetter");

        var count = await buffer.GetCountAsync();

        Assert.Equal(1, count);
    }

    /// <summary>P1-6：死信查询返回条目（批次上下文 + 重试次数）</summary>
    [Fact]
    public async Task GetDeadLetters_ReturnsEntries()
    {
        using var db = new TempForwardBufferDb();
        var batch = NewBatch(Guid.NewGuid());
        InsertRow(db.ConnectionString, batch.Id.ToString(), Serialize(batch), "DeadLetter", retryCount: 3);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.GetDeadLettersAsync(10);

        Assert.True(result.IsSuccess);
        var entry = Assert.Single(result.Value!);
        Assert.Equal(batch.Id, entry.BatchId);
        Assert.Equal(batch.DeviceId, entry.DeviceId);
        Assert.Equal(batch.Records.Count, entry.RecordCount);
        Assert.Equal(3, entry.RetryCount);
    }

    /// <summary>P1-6：死信重试 → 回到 Pending 并清零重试计数</summary>
    [Fact]
    public async Task RetryDeadLetter_MovesBackToPending()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "{}", "DeadLetter", retryCount: 6);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.RetryDeadLetterAsync(Guid.Parse(batchId));

        Assert.True(result.IsSuccess);
        var (status, retryCount, _) = ReadRow(db.ConnectionString, batchId);
        Assert.Equal("Pending", status);
        Assert.Equal(0, retryCount);
    }

    /// <summary>P1-6：死信丢弃 → 物理删除；不存在的死信返回 NotFound</summary>
    [Fact]
    public async Task DiscardDeadLetter_RemovesRow()
    {
        using var db = new TempForwardBufferDb();
        var batchId = Guid.NewGuid().ToString();
        InsertRow(db.ConnectionString, batchId, "{}", "DeadLetter");
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.DiscardDeadLetterAsync(Guid.Parse(batchId));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, buffer.Count);

        var missing = await buffer.DiscardDeadLetterAsync(Guid.NewGuid());
        Assert.True(missing.IsFailure);
        Assert.Equal(ErrorCategory.General, missing.Error!.Category);
        Assert.Equal("NotFound", missing.Error.Code);
    }

    /// <summary>P1-6：Commit 删除已确认批次，队列回到空</summary>
    [Fact]
    public async Task Commit_DeletesBatches()
    {
        using var db = new TempForwardBufferDb();
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);
        var batch = NewBatch(Guid.NewGuid());
        await buffer.EnqueueAsync(batch);

        var dequeued = await buffer.DequeueAsync(10);
        Assert.True(dequeued.IsSuccess);
        Assert.Single(dequeued.Value!);

        var commit = await buffer.CommitAsync([batch.Id]);

        Assert.True(commit.IsSuccess);
        Assert.Equal(0, buffer.Count);

        using var connection = Open(db.ConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM forward_buffer WHERE id = @id;";
        command.Parameters.AddWithValue("@id", batch.Id.ToString());
        Assert.Equal(0L, command.ExecuteScalar());
    }

    /// <summary>P1-1：死信查询在表缺失时返回分类失败，不向调用方抛异常</summary>
    [Fact]
    public async Task GetDeadLetters_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempForwardBufferDb();
        DropForwardBufferTable(db.ConnectionString);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.GetDeadLettersAsync(10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("死信查询失败", result.Error.Message);
    }

    /// <summary>P1-1：死信重试在表缺失时返回分类失败，不向调用方抛异常</summary>
    [Fact]
    public async Task RetryDeadLetter_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempForwardBufferDb();
        DropForwardBufferTable(db.ConnectionString);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.RetryDeadLetterAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("死信重试失败", result.Error.Message);
    }

    /// <summary>P1-1：死信丢弃在表缺失时返回分类失败，不向调用方抛异常</summary>
    [Fact]
    public async Task DiscardDeadLetter_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempForwardBufferDb();
        DropForwardBufferTable(db.ConnectionString);
        var buffer = new SqliteForwardBuffer(db.ConnectionString, NullLogger<SqliteForwardBuffer>.Instance);

        var result = await buffer.DiscardDeadLetterAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("死信丢弃失败", result.Error.Message);
    }
}
