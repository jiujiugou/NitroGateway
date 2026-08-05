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

/// <summary>内存转发缓冲：记录待转发批次与已提交批次</summary>
public sealed class FakeForwardBuffer : IForwardBuffer
{
    public List<BatchMeasurements> Pending { get; } = [];
    public List<Guid> Committed { get; } = [];

    public int Count => Pending.Count;

    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
    {
        Pending.Add(batch);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(
        int maxCount, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<BatchMeasurements>>.Success(Pending.Take(maxCount).ToList()));

    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
    {
        Pending.RemoveAll(b => batchIds.Contains(b.Id));
        Committed.AddRange(batchIds);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

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