using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-026 D2：EventBridge 200ms 帧合并单测。
/// 帧间隔注入 1 小时，避免后台循环干扰手动 Flush 的确定性。
/// </summary>
public sealed class EventBridgeTests : IDisposable
{
    private static readonly TimeSpan LongFrame = TimeSpan.FromHours(1);
    private readonly FakeForwardBuffer _buffer = new();
    private EventBridge? _bridge;

    [Fact]
    public void OnStoredAsync_then_Flush_publishes_merged_frame()
    {
        var bridge = CreateBridge();
        UiFrame? received = null;
        bridge.FrameReady += f => received = f;

        var deviceId = Guid.NewGuid();
        bridge.OnStoredAsync(new PointStoredEvent { DeviceId = deviceId, Snapshots = [Snapshot("p1"), Snapshot("p2")] });
        bridge.OnStoredAsync(new PointStoredEvent { DeviceId = deviceId, Snapshots = [Snapshot("p3")] });
        bridge.Flush();

        Assert.NotNull(received);
        Assert.Equal(3, received!.Measurements.Count);
    }

    [Fact]
    public void Flush_without_events_does_not_publish_empty_frame()
    {
        var bridge = CreateBridge();
        var published = 0;
        bridge.FrameReady += _ => published++;

        bridge.Flush();

        Assert.Equal(0, published);
    }

    [Fact]
    public void Mqtt_state_and_health_changes_are_carried_in_frame()
    {
        var bridge = CreateBridge();
        UiFrame? received = null;
        bridge.FrameReady += f => received = f;

        var deviceId = Guid.NewGuid();
        bridge.OnStateChangedAsync(MqttConnectionState.Connected);
        bridge.OnHealthChangedAsync(new DeviceHealthChanged
        {
            DeviceId = deviceId,
            OldStatus = DeviceStatus.Offline,
            NewStatus = DeviceStatus.Online
        });
        bridge.Flush();

        Assert.NotNull(received);
        Assert.Equal(MqttConnectionState.Connected, received!.MqttState);
        var change = Assert.Single(received.HealthChanges);
        Assert.Equal(DeviceStatus.Online, change.NewStatus);
    }

    [Fact]
    public async Task RefreshBacklogAsync_carries_changed_backlog_in_next_frame()
    {
        var bridge = CreateBridge();
        _buffer.Count = 42;
        await bridge.RefreshBacklogAsync();

        UiFrame? received = null;
        bridge.FrameReady += f => received = f;
        bridge.Flush();

        Assert.Equal(42, received!.BufferBacklog);
    }

    [Fact]
    public async Task Unchanged_backlog_is_not_republished()
    {
        var bridge = CreateBridge();
        _buffer.Count = 42;
        await bridge.RefreshBacklogAsync();
        bridge.Flush();

        var published = 0;
        bridge.FrameReady += _ => published++;
        await bridge.RefreshBacklogAsync();
        bridge.Flush();

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task Backlog_change_after_previous_flush_is_published()
    {
        var bridge = CreateBridge();
        _buffer.Count = 10;
        await bridge.RefreshBacklogAsync();
        bridge.Flush();

        _buffer.Count = 25;
        await bridge.RefreshBacklogAsync();

        UiFrame? received = null;
        bridge.FrameReady += f => received = f;
        bridge.Flush();

        Assert.Equal(25, received!.BufferBacklog);
    }

    private EventBridge CreateBridge()
    {
        _bridge = new EventBridge(_buffer, NullLogger<EventBridge>.Instance, LongFrame);
        return _bridge;
    }

    private static PointSnapshot Snapshot(string pointName) => new()
    {
        DeviceId = Guid.NewGuid(),
        DevicePointId = Guid.NewGuid(),
        PointName = pointName,
        DataType = DataType.Float,
        Value = 1.0,
        Timestamp = DateTime.UtcNow,
        Quality = QualityCode.Good
    };

    public void Dispose()
    {
        _bridge?.Dispose();
        _bridge = null;
    }

    /// <summary>IForwardBuffer 替身：仅 Count/GetCountAsync 可用，其余抛不支持。</summary>
    private sealed class FakeForwardBuffer : IForwardBuffer
    {
        public int Count { get; set; }

        public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(Count);

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}


