using NitroGateway.Domain.Devices;

namespace NitroGateway.Domain.Events;

/// <summary>
/// 点位数据已存储事件。由 Dispatcher 在数据写入后发布。
/// 订阅方（Alarm、Statistics、Audit 等）通过实现 <see cref="IPointStoredSink"/> 接收。
/// </summary>
public sealed record PointStoredEvent
{
    /// <summary>设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>本轮采集的快照</summary>
    public IReadOnlyList<PointSnapshot> Snapshots { get; init; } = Array.Empty<PointSnapshot>();
}
