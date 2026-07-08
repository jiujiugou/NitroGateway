namespace NitroGateway.Domain.Events;

/// <summary>
/// 设备配置变更回调。由 DeviceManager/PointManager 在持久化后同步调用。
/// 实现类以 Singleton 注册，变更通知不走 Channel，直接同步触发（配置变更是低频操作）。
/// </summary>
public interface IDeviceChangeSink
{
    /// <summary>处理设备配置变更</summary>
    void OnDeviceChanged(DeviceChangeEvent e);
}
