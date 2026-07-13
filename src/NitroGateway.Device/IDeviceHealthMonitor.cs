namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定。计数 + 阈值触发 + 快照查询</summary>
public interface IDeviceHealthMonitor
{
    /// <summary>上报一次成功采集</summary>
    void ReportSuccess(Guid deviceId);

    /// <summary>上报一次失败采集</summary>
    void ReportFailure(Guid deviceId, string reason);

    /// <summary>更新设备状态（由 DeviceManager 状态变更触发）</summary>
    void UpdateStatus(Guid deviceId, Domain.Devices.DeviceStatus status);

    /// <summary>连续失败多少次触发 Offline</summary>
    int FailureThreshold { get; }

    /// <summary>连续成功多少次触发 Online 恢复</summary>
    int RecoveryThreshold { get; }

    /// <summary>获取单设备健康快照</summary>
    DeviceHealthSnapshot? GetSnapshot(Guid deviceId);

    /// <summary>获取所有设备健康快照</summary>
    IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots();

    /// <summary>触发状态变更时调用此回调（Offline / Online）</summary>
    event Action<Guid, Domain.Devices.DeviceStatus>? StatusChanged;
}
