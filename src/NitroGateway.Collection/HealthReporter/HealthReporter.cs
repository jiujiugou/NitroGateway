using NitroGateway.DeviceManagement;

namespace NitroGateway.Collection;

/// <summary>
/// 健康上报实现：把单次采集的成功/失败点数汇总为一次成功或失败信号，转发给
/// <see cref="IDeviceHealthMonitor"/> 做设备健康状态判定。
/// <para><b>边界：</b>上报异常被吞掉——健康上报失败不能中断采集主循环。</para>
/// </summary>
public sealed class HealthReporter : IHealthReporter
{
    private readonly IDeviceHealthMonitor _healthMonitor;

    /// <summary>创建健康上报器。</summary>
    /// <param name="healthMonitor">健康监控；负责状态迁移与监听器通知</param>
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
