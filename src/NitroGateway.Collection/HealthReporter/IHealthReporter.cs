namespace NitroGateway.Collection;

/// <summary>健康上报：汇总本轮采集结果上报给 DeviceHealthMonitor</summary>
public interface IHealthReporter
{
    void Report(Guid deviceId, int successCount, int failCount, string? errorMessage);
}
