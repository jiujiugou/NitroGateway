using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

public class DeviceHealthMonitorTests
{
    private readonly Guid _deviceId = Guid.NewGuid();

    [Fact]
    public void TenFailures_TriggersOffline()
    {
        var triggered = false;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.StatusChanged += (_, s) => { if (s == DeviceStatus.Offline) triggered = true; };

        for (var i = 0; i < 9; i++) monitor.ReportFailure(_deviceId, "timeout");
        Assert.False(triggered);

        monitor.ReportFailure(_deviceId, "timeout");
        Assert.True(triggered);
    }

    [Fact]
    public void ThreeSuccess_TriggersOnline()
    {
        var triggered = false;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.StatusChanged += (_, s) => { if (s == DeviceStatus.Online) triggered = true; };

        // 先触发离线
        for (var i = 0; i < 10; i++) monitor.ReportFailure(_deviceId, "timeout");

        // 恢复
        for (var i = 0; i < 2; i++) monitor.ReportSuccess(_deviceId);
        Assert.False(triggered);

        monitor.ReportSuccess(_deviceId);
        Assert.True(triggered);
    }

    [Fact]
    public void SuccessResetsFailCount()
    {
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        for (var i = 0; i < 9; i++) monitor.ReportFailure(_deviceId, "timeout");
        monitor.ReportSuccess(_deviceId);   // 重置失败计数

        var triggered = false;
        monitor.StatusChanged += (_, s) => { if (s == DeviceStatus.Offline) triggered = true; };
        monitor.ReportFailure(_deviceId, "timeout");
        Assert.False(triggered);  // 不会触发，因为之前被重置了
    }

    [Fact]
    public void FailureResetsSuccessCount()
    {
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        for (var i = 0; i < 2; i++) monitor.ReportSuccess(_deviceId);

        var triggered = false;
        monitor.StatusChanged += (_, s) => { if (s == DeviceStatus.Online) triggered = true; };
        monitor.ReportFailure(_deviceId, "timeout"); // 重置成功计数
        monitor.ReportSuccess(_deviceId);
        Assert.False(triggered);  // 只成功了1次，不够3次
    }

    [Fact]
    public void GetConsecutiveFailures_ReturnsCorrectCount()
    {
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.ReportFailure(_deviceId, "a");
        monitor.ReportFailure(_deviceId, "b");
        Assert.Equal(2, monitor.GetConsecutiveFailures(_deviceId));
    }
}
