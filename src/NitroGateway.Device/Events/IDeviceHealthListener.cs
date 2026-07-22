namespace NitroGateway.DeviceManagement.Events;

/// <summary>
/// 设备健康状态变更监听器。HealthMonitor 在状态迁移时遍历所有注册的 Listener。
/// 实现类注册为 Singleton，每个 Listener 的异常不影响其他。
/// </summary>
public interface IDeviceHealthListener
{
    /// <summary>处理健康状态变更</summary>
    ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default);
}
