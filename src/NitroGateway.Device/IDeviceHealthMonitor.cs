using NitroGateway.DeviceManagement.Events;

namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定。HealthMonitor 是 SST——唯一负责 Online/Offline 状态转换。</summary>
public interface IDeviceHealthMonitor
{
    /// <summary>上报一次成功采集</summary>
    void ReportSuccess(Guid deviceId);

    /// <summary>上报一次失败采集</summary>
    void ReportFailure(Guid deviceId, string reason);

    /// <summary>更新快照中的维护状态</summary>
    void UpdateStatus(Guid deviceId, Domain.Devices.DeviceStatus status);

    /// <summary>连续失败多少次触发离线</summary>
    int FailureThreshold { get; }

    /// <summary>连续成功多少次触发恢复</summary>
    int RecoveryThreshold { get; }

    /// <summary>获取单设备健康快照</summary>
    DeviceHealthSnapshot? GetSnapshot(Guid deviceId);

    /// <summary>获取所有设备健康快照</summary>
    IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots();

    /// <summary>注册监听器</summary>
    void AddListener(IDeviceHealthListener listener);
}
