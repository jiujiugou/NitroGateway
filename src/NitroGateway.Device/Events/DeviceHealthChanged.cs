using NitroGateway.Domain.Devices;

namespace NitroGateway.DeviceManagement.Events;

/// <summary>设备健康状态变更事件。HealthMonitor 在状态迁移时发布。</summary>
public sealed record DeviceHealthChanged
{
    /// <summary>设备 ID</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>设备名称（来自采集上报；可能为空，日志定位设备时与 DeviceId 一并输出）</summary>
    public string? DeviceName { get; init; }

    /// <summary>旧状态</summary>
    public DeviceStatus OldStatus { get; init; }

    /// <summary>新状态</summary>
    public required DeviceStatus NewStatus { get; init; }
}
