using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.IntegrationTests;

/// <summary>内存测量存储：记录写入的快照</summary>
public sealed class FakeMeasurementStore : IMeasurementStore
{
    public List<PointSnapshot> Written { get; } = [];

    public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
    {
        Written.AddRange(snapshots);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));

    public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}

/// <summary>
/// 内存转发缓冲：记录待转发批次与已提交批次。
/// 支持注入 Dequeue/Commit/MarkFailed 失败（ADR-001 P1-3 失败路径测试用）。
/// </summary>
public sealed class FakeForwardBuffer : IForwardBuffer
{
    public List<BatchMeasurements> Pending { get; } = [];
    public List<Guid> Committed { get; } = [];
    public List<(Guid BatchId, string Reason)> MarkedFailed { get; } = [];

    /// <summary>注入出队失败，非 null 时 DequeueAsync 返回该失败</summary>
    public OperationalError? DequeueError { get; set; }

    /// <summary>注入提交失败，非 null 时 CommitAsync 返回该失败</summary>
    public OperationalError? CommitError { get; set; }

    /// <summary>注入标记失败失败，非 null 时 MarkFailedAsync 返回该失败</summary>
    public OperationalError? MarkFailedError { get; set; }

    public int Count => Pending.Count;

    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        Pending.Add(batch);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
        int maxCount, CancellationToken ct = default)
    {
        if (DequeueError is not null)
            return Task.FromResult(OperationResult<IReadOnlyList<BatchMeasurements>>.Failure(DequeueError));
        return Task.FromResult(OperationResult<IReadOnlyList<BatchMeasurements>>.Success(Pending.Take(maxCount).ToList()));
    }

    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        if (CommitError is not null)
            return Task.FromResult(OperationResult.Failure(CommitError));
        Pending.RemoveAll(b => batchIds.Contains(b.Id));
        Committed.AddRange(batchIds);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
    {
        MarkedFailed.Add((batchId, reason));
        return MarkFailedError is not null
            ? Task.FromResult(OperationResult.Failure(MarkFailedError))
            : Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(
        int maxCount, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<DeadLetterEntry>>.Success([]));

    public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}

/// <summary>记录发布消息的 MQTT 替身</summary>
public sealed class FakeMqttClient : IMqttClient
{
    public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
    public List<(string Topic, byte[] Payload)> Published { get; } = [];

    /// <summary>注入发布失败，非 null 时 PublishAsync 返回该失败</summary>
    public OperationResult? PublishResult { get; set; }

    public event Action<MqttConnectionState>? StateChanged;

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        State = MqttConnectionState.Connected;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        State = MqttConnectionState.Disconnected;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
    {
        if (PublishResult is not null)
            return Task.FromResult(PublishResult);
        Published.Add((topic, payload));
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

    private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>捕获日志条目（ILogger 替身），用于断言失败路径是否记录 Error 级日志</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
