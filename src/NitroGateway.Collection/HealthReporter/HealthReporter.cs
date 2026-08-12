using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;

namespace NitroGateway.Collection;

/// <summary>
/// 健康上报实现：把单次采集的链路结果（成功/失败）转成一次成功或失败信号，转发给
/// <see cref="IDeviceHealthMonitor"/> 做设备健康状态判定。
/// <para><b>边界：</b>上报异常被吞掉并记 Warning——健康上报失败不能中断采集主循环，但必须留日志线索。</para>
/// </summary>
public sealed class HealthReporter : IHealthReporter
{
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly ILogger<HealthReporter> _logger;

    /// <summary>创建健康上报器。</summary>
    /// <param name="healthMonitor">健康监控；负责状态迁移与监听器通知</param>
    /// <param name="logger">日志；上报异常时记录，避免静默</param>
    public HealthReporter(IDeviceHealthMonitor healthMonitor, ILogger<HealthReporter> logger)
    {
        _healthMonitor = healthMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage)
    {
        try
        {
            if (succeeded)
                _healthMonitor.ReportSuccess(deviceId, deviceName);
            else
                _healthMonitor.ReportFailure(deviceId, deviceName, errorMessage ?? "采集失败");
        }
        catch (Exception ex)
        {
            // ADR-031：健康上报异常不能崩采集循环，但不能静默——否则设备状态滞留且无日志线索
            _logger.LogWarning(ex, "健康上报异常被吞掉: Device={DeviceName} [{DeviceId}]", deviceName, deviceId);
        }
    }
}
