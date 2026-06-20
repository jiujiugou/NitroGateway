namespace NitroGateway.DeviceManagement;

/// <summary>设备健康判定。只计数，不修改 Device 状态</summary>
public interface IDeviceHealthMonitor
{
    void ReportSuccess(Guid deviceId);
    void ReportFailure(Guid deviceId, string reason);
    int FailureThreshold { get; }
    int RecoveryThreshold { get; }

    /// <summary>触发状态变更时调用此回调</summary>
    event Action<Guid, Domain.Devices.DeviceStatus>? ThresholdReached;
}
