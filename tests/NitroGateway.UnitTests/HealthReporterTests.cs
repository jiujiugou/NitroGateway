using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// HealthReporter 测试（ADR-031）：设备健康只看链路成功/失败；
/// 点位级质量差不参与判定；上报异常被吞掉但必须留日志，不能静默。
/// </summary>
public class HealthReporterTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();

    [Fact]
    public void Report_True_ReportsSuccess()
    {
        var monitor = new FakeMonitor();
        var reporter = new HealthReporter(monitor, NullLogger<HealthReporter>.Instance);

        reporter.Report(DeviceId, "PLC", true, null);

        Assert.Equal(1, monitor.Successes);
        Assert.Equal(0, monitor.Failures);
    }

    [Fact]
    public void Report_False_ReportsFailure_WithReason()
    {
        var monitor = new FakeMonitor();
        var reporter = new HealthReporter(monitor, NullLogger<HealthReporter>.Instance);

        reporter.Report(DeviceId, "PLC", false, "从站无响应");

        Assert.Equal(1, monitor.Failures);
        Assert.Equal("从站无响应", monitor.LastReason);
    }

    [Fact]
    public void Report_False_NoReason_UsesDefaultMessage()
    {
        var monitor = new FakeMonitor();
        var reporter = new HealthReporter(monitor, NullLogger<HealthReporter>.Instance);

        reporter.Report(DeviceId, null, false, null);

        Assert.Equal(1, monitor.Failures);
        Assert.Equal("采集失败", monitor.LastReason);
    }

    [Fact]
    public void Report_HealthMonitorThrows_DoesNotPropagate()
    {
        var monitor = new ThrowingMonitor();
        var reporter = new HealthReporter(monitor, NullLogger<HealthReporter>.Instance);

        reporter.Report(DeviceId, "PLC", false, "boom"); // 不应抛出，采集热循环不中断
    }

    private sealed class FakeMonitor : IDeviceHealthMonitor
    {
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public string? LastReason { get; private set; }
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void ReportSuccess(Guid deviceId, string? deviceName) => Successes++;
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { Failures++; LastReason = reason; }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) { }
    }

    private sealed class ThrowingMonitor : IDeviceHealthMonitor
    {
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) => throw new InvalidOperationException("monitor boom");
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) { }
    }
}
