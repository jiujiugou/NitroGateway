using NitroGateway.Domain.Devices;

namespace NitroGateway.Alarm.Events;

/// <summary>
/// 点位数据已存储事件。由 Dispatcher 在数据写入后发布，AlarmHostedService 订阅后触发告警评估。
/// 使用 Channel 实现异步解耦，不阻塞采集流程。
/// </summary>
public sealed record PointStoredEvent
{
    /// <summary>设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>点位快照列表</summary>
    public IReadOnlyList<PointSnapshot> Snapshots { get; init; } = Array.Empty<PointSnapshot>();
}
