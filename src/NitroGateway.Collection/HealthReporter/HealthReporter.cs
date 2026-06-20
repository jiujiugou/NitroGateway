using NitroGateway.DeviceManagement;

namespace NitroGateway.Collection;

/// <summary>健康上报实现</summary>
public sealed class HealthReporter : IHealthReporter
{
    private readonly IDeviceHealthMonitor _healthMonitor;

    public HealthReporter(IDeviceHealthMonitor healthMonitor)
    {
        _healthMonitor = healthMonitor;
    }

    /// <inheritdoc />
    public void Report(Guid deviceId, int successCount, int failCount, string? errorMessage)
    {
        try
        {
            if (failCount > 0)
                _healthMonitor.ReportFailure(deviceId, errorMessage ?? "采集失败");
            else
                _healthMonitor.ReportSuccess(deviceId);
        }
        catch
        {
            // 健康上报失败不能崩采集循环
        }
    }
}
