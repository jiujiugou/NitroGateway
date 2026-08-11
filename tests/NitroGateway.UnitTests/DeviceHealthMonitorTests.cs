using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

public class DeviceHealthMonitorTests
{
    private readonly Guid _deviceId = Guid.NewGuid();

    /// <summary>3 次失败（阈值=3）→ Listener 收到 Offline</summary>
    [Fact]
    public void ThreeFailures_TriggersOffline()
    {
        DeviceHealthChanged? lastEvent = null;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.UpdateStatus(_deviceId, DeviceStatus.Online);
        monitor.AddListener(new TestListener(e => lastEvent = e));

        for (var i = 0; i < 2; i++) monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        Assert.Null(lastEvent);

        monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        Assert.NotNull(lastEvent);
        Assert.Equal(DeviceStatus.Offline, lastEvent!.NewStatus);
        Assert.Equal("测试设备", lastEvent.DeviceName);
    }

    /// <summary>3 次成功 → Listener 收到 Online</summary>
    [Fact]
    public void ThreeSuccess_TriggersOnline()
    {
        DeviceHealthChanged? lastEvent = null;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        for (var i = 0; i < 3; i++) monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        monitor.UpdateStatus(_deviceId, DeviceStatus.Offline);
        monitor.AddListener(new TestListener(e => lastEvent = e));

        for (var i = 0; i < 2; i++) monitor.ReportSuccess(_deviceId, "测试设备");
        Assert.Null(lastEvent);

        monitor.ReportSuccess(_deviceId, "测试设备");
        Assert.NotNull(lastEvent);
        Assert.Equal(DeviceStatus.Online, lastEvent!.NewStatus);
        Assert.Equal("测试设备", lastEvent.DeviceName);
    }

    /// <summary>一次成功重置失败计数——3 次失败前有 1 次成功，不触发 Offline</summary>
    [Fact]
    public void SuccessResetsFailCount()
    {
        DeviceHealthChanged? lastEvent = null;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.UpdateStatus(_deviceId, DeviceStatus.Online);
        for (var i = 0; i < 2; i++) monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        monitor.ReportSuccess(_deviceId, "测试设备"); // 重置
        monitor.AddListener(new TestListener(e => lastEvent = e));
        monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        Assert.Null(lastEvent);
    }

    /// <summary>一次失败重置成功计数——2 次成功后一次失败，Online 不触发</summary>
    [Fact]
    public void FailureResetsSuccessCount()
    {
        DeviceHealthChanged? lastEvent = null;
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.UpdateStatus(_deviceId, DeviceStatus.Offline);
        for (var i = 0; i < 2; i++) monitor.ReportSuccess(_deviceId, "测试设备");
        monitor.ReportFailure(_deviceId, "测试设备", "reset");
        monitor.AddListener(new TestListener(e => lastEvent = e));
        monitor.ReportSuccess(_deviceId, "测试设备");
        Assert.Null(lastEvent);
    }

    /// <summary>连续失败次数正确</summary>
    [Fact]
    public void GetConsecutiveFailures_ReturnsCorrectCount()
    {
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.ReportFailure(_deviceId, "测试设备", "a");
        monitor.ReportFailure(_deviceId, "测试设备", "b");
        Assert.Equal(2, monitor.GetConsecutiveFailures(_deviceId));
    }

    /// <summary>注销设备后 Remove 应清空快照与计数（防内存泄漏与幽灵设备）</summary>
    [Fact]
    public void Remove_ClearsCountersAndSnapshot()
    {
        var monitor = new DeviceHealthMonitor(NullLogger<DeviceHealthMonitor>.Instance);
        monitor.UpdateStatus(_deviceId, DeviceStatus.Online);
        monitor.ReportFailure(_deviceId, "测试设备", "timeout");
        monitor.ReportSuccess(_deviceId, "测试设备");

        monitor.Remove(_deviceId);

        Assert.Null(monitor.GetSnapshot(_deviceId));
        Assert.Equal(0, monitor.GetConsecutiveFailures(_deviceId));
        Assert.Equal(0, monitor.GetConsecutiveSuccesses(_deviceId));
        Assert.Empty(monitor.GetAllSnapshots());
    }

    private sealed class TestListener(Action<DeviceHealthChanged> onChanged) : IDeviceHealthListener
    {
        public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
        {
            onChanged(e);
            return ValueTask.CompletedTask;
        }
    }
}
