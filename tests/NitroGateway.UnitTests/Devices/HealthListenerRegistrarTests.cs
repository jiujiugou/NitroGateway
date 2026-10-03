using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.DeviceManagement.Listeners;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests.Devices;

/// <summary>启动时把 DI 中所有 IDeviceHealthListener 注册到 HealthMonitor。</summary>
public class HealthListenerRegistrarTests
{
    private sealed class RecordingMonitor : IDeviceHealthMonitor
    {
        public List<IDeviceHealthListener> Added { get; } = [];

        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) => Added.Add(listener);
    }

    private sealed class NoopListener : IDeviceHealthListener
    {
        public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    [Fact]
    public void Constructor_RegistersAllListeners()
    {
        var monitor = new RecordingMonitor();
        var a = new NoopListener();
        var b = new NoopListener();

        _ = new HealthListenerRegistrar(monitor, [a, b]);

        Assert.Equal(2, monitor.Added.Count);
        Assert.Contains(a, monitor.Added);
        Assert.Contains(b, monitor.Added);
    }
}
