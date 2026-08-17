using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Webapi.Hubs;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// DeviceStatusDispatcher（SignalR 出口）ADR-053 接线单测。
/// 语义：PersistedSnapshots 只推"放行子集"；null 回退 Snapshots 全量（兼容旧调用方）；
/// 空列表（全抑制）直接跳过不推送——避免给前端推空包/造成"已清空"假象。
/// </summary>
public class DeviceStatusDispatcherTests
{
    private static DeviceStatusDispatcher Create(out Channel<OutboxMessage> channel)
    {
        channel = Channel.CreateUnbounded<OutboxMessage>();
        return new DeviceStatusDispatcher(channel, NullLogger<DeviceStatusDispatcher>.Instance);
    }

    private static PointSnapshot Snap(Guid deviceId, Guid pointId, object? value) => new()
    {
        DeviceId = deviceId,
        DevicePointId = pointId,
        PointName = "P",
        DataType = DataType.Float,
        Value = value,
        Timestamp = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
        Quality = QualityCode.Good
    };

    /// <summary>ADR-053：全抑制（PersistedSnapshots 空）→ 直接跳过，不写 Outbox，前端不再每秒收空包。</summary>
    [Fact]
    public async Task OnStoredAsync_AllSuppressed_EmptyPersisted_SkipsPush()
    {
        var dispatcher = Create(out var channel);
        var deviceId = Guid.NewGuid();
        var ev = new PointStoredEvent
        {
            DeviceId = deviceId,
            Snapshots = [Snap(deviceId, Guid.NewGuid(), 10.0), Snap(deviceId, Guid.NewGuid(), 20.0)],
            PersistedSnapshots = []
        };

        await dispatcher.OnStoredAsync(ev);

        Assert.False(channel.Reader.TryRead(out _));
    }

    /// <summary>ADR-053 兼容：PersistedSnapshots=null（旧调用方/未启用抑制）→ 回退 Snapshots 全量推送。</summary>
    [Fact]
    public async Task OnStoredAsync_NullPersisted_FallsBackToFullSnapshots()
    {
        var dispatcher = Create(out var channel);
        var deviceId = Guid.NewGuid();
        var ev = new PointStoredEvent
        {
            DeviceId = deviceId,
            Snapshots = [Snap(deviceId, Guid.NewGuid(), 1.0), Snap(deviceId, Guid.NewGuid(), 2.0)],
            PersistedSnapshots = null
        };

        await dispatcher.OnStoredAsync(ev);

        Assert.True(channel.Reader.TryRead(out var msg));
        Assert.False(channel.Reader.TryRead(out _)); // 只有一条
        Assert.Equal("Measurement", msg.Method);
        Assert.Equal(OutboxTarget.Group, msg.TargetType);
        Assert.Equal(deviceId.ToString(), msg.GroupId);
        var payload = Assert.IsType<List<OutboxMeasurement>>(msg.Payload);
        Assert.Equal(2, payload.Count);
    }

    /// <summary>ADR-053：只推"放行子集"（PersistedSnapshots），字段映射完整（DevicePointId/DeviceId/Value/Quality/Timestamp）。</summary>
    [Fact]
    public async Task OnStoredAsync_PersistedSubset_PushesOnlyPassed()
    {
        var dispatcher = Create(out var channel);
        var deviceId = Guid.NewGuid();
        var passed = Guid.NewGuid();
        var ts = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);
        var ev = new PointStoredEvent
        {
            DeviceId = deviceId,
            Snapshots = [Snap(deviceId, passed, 11.5), Snap(deviceId, Guid.NewGuid(), 10.2)],
            PersistedSnapshots =
            [
                new PointSnapshot
                {
                    DeviceId = deviceId,
                    DevicePointId = passed,
                    PointName = "P",
                    DataType = DataType.Float,
                    Value = 11.5,
                    Timestamp = ts,
                    Quality = QualityCode.Good
                }
            ]
        };

        await dispatcher.OnStoredAsync(ev);

        Assert.True(channel.Reader.TryRead(out var msg));
        var payload = Assert.IsType<List<OutboxMeasurement>>(msg.Payload);
        var m = Assert.Single(payload);
        Assert.Equal(passed.ToString(), m.DevicePointId);
        Assert.Equal(deviceId.ToString(), m.DeviceId);
        Assert.Equal(11.5, m.Value);
        Assert.Equal(QualityCode.Good.ToString(), m.Quality);
        Assert.Equal(ts.ToString("O"), m.Timestamp);
    }
}
