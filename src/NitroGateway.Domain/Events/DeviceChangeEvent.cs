namespace NitroGateway.Domain.Events;

/// <summary>配置变更类型</summary>
public enum DeviceChangeType
{
    /// <summary>设备新增</summary>
    Added,
    /// <summary>设备更新(含状态变更)</summary>
    Updated,
    /// <summary>设备移除</summary>
    Removed,
    /// <summary>设备点位变更</summary>
    PointsChanged
}

/// <summary>
/// 设备配置变更事件。由 DeviceManager/PointManager 在持久化后发布。
/// DeviceCache 订阅后增量更新运行时缓存，无需重新全量加载。
/// </summary>
public sealed record DeviceChangeEvent
{
    /// <summary>变更类型</summary>
    public DeviceChangeType Type { get; init; }

    /// <summary>受影响的设备 ID</summary>
    public Guid DeviceId { get; init; }
}
