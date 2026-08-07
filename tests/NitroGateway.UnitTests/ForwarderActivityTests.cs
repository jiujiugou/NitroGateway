using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry.Tracing;
using NitroGateway.Transport.MQTT;
using Xunit;
using ForwarderImpl = NitroGateway.Forwarder.Forwarder;

namespace NitroGateway.UnitTests;

/// <summary>
/// Forwarder Activity 状态测试（ADR-001 P2-9）：
/// 失败路径（Dequeue 失败 / Publish 失败）必须 SetStatus(Error, 原因)，
/// 成功路径才置 Ok，Forward 追踪不再恒为 Ok。
/// </summary>
public class ForwarderActivityTests
{
    private sealed class FakeBuffer : IForwardBuffer
    {
        public List<BatchMeasurements> Pending { get; } = [];
        public OperationalError? DequeueError { get; set; }

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
            Pending.RemoveAll(b => batchIds.Contains(b.Id));
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

    private sealed class FakeMqtt : IMqttClient
    {
        public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
        public OperationResult? PublishResult { get; set; }

        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
            => PublishResult is not null
                ? Task.FromResult(PublishResult)
                : Task.FromResult(OperationResult.Success());

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static BatchMeasurements NewBatch() => new()
    {
        Id = Guid.NewGuid(),
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

    private static ActivityListener StartListener(out List<Activity> forwardActivities)
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GatewayActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        listener.ActivityStopped = activity =>
        {
            if (activity.OperationName == GatewayActivities.Forward)
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        forwardActivities = captured;
        return listener;
    }

    private static ForwarderImpl CreateForwarder(FakeBuffer buffer, FakeMqtt mqtt)
        => new(buffer, new JsonMessageSerializer(), mqtt, new ForwardingThrottle(), NullLogger<ForwarderImpl>.Instance);

    /// <summary>成功转发置 Ok</summary>
    [Fact]
    public async Task ForwardBatchAsync_Success_SetsActivityOk()
    {
        using var listener = StartListener(out var activities);
        var buffer = new FakeBuffer();
        await buffer.EnqueueAsync(NewBatch());
        var forwarder = CreateForwarder(buffer, new FakeMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    /// <summary>空队列是正常状态，置 Ok</summary>
    [Fact]
    public async Task ForwardBatchAsync_EmptyQueue_SetsActivityOk()
    {
        using var listener = StartListener(out var activities);
        var forwarder = CreateForwarder(new FakeBuffer(), new FakeMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    /// <summary>Publish 失败置 Error 并携带失败原因</summary>
    [Fact]
    public async Task ForwardBatchAsync_PublishFailure_SetsActivityError()
    {
        using var listener = StartListener(out var activities);
        var buffer = new FakeBuffer();
        await buffer.EnqueueAsync(NewBatch());
        var mqtt = new FakeMqtt
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("broker 不可达"))
        };
        var forwarder = CreateForwarder(buffer, mqtt);

        await forwarder.ForwardBatchAsync(10);

        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains("broker 不可达", activity.StatusDescription);
    }

    /// <summary>Dequeue 失败置 Error 并携带失败原因</summary>
    [Fact]
    public async Task ForwardBatchAsync_DequeueFailure_SetsActivityError()
    {
        using var listener = StartListener(out var activities);
        var buffer = new FakeBuffer { DequeueError = OperationalError.Storage("Buffer 出队失败") };
        var forwarder = CreateForwarder(buffer, new FakeMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsFailure);
        var activity = Assert.Single(activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains("出队", activity.StatusDescription);
    }
}
