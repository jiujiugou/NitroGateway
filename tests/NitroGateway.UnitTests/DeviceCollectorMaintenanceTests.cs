using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-002 P2-2（方案 1）：DeviceCollector 的维护模式过滤以 HealthMonitor 实时状态为准，
/// 不依赖设备目录缓存中的 Status（配置缓存可能滞后一个采集周期）。
/// </summary>
public class DeviceCollectorMaintenanceTests
{
    private readonly FakeDeviceManager _manager = new();
    private readonly FakeDeviceReader _reader = new();
    private readonly FakeHealthMonitor _monitor = new();

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:IntervalMs"] = "1000",
                ["Collection:MaxConcurrency"] = "1"
            })
            .Build());
        services.AddSingleton<IDeviceManager>(_manager);
        services.AddSingleton<IDeviceReader>(_reader);
        services.AddSingleton<IPointValuePipeline>(new FakePipeline());
        services.AddSingleton<IDataDispatcher>(new FakeDispatcher());
        services.AddSingleton<IHealthReporter>(new FakeReporter());
        services.AddSingleton<IDeviceHealthMonitor>(_monitor);
        return services.BuildServiceProvider();
    }

    /// <summary>配置状态 Online，但 HealthMonitor 实时状态为 Maintenance → 跳过采集</summary>
    [Fact]
    public async Task CollectOnceAsync_HealthMonitorSaysMaintenance_SkipsDevice()
    {
        var device = MakeDevice("PLC");
        _manager.Devices.Add(device);
        _monitor.Statuses[device.Id] = DeviceStatus.Maintenance;

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectOnceAsync(CancellationToken.None);

        Assert.Empty(_reader.ReadDevices);
    }

    /// <summary>配置 Status=Maintenance，但 HealthMonitor 实时状态为 Online → 仍然采集</summary>
    [Fact]
    public async Task CollectOnceAsync_ConfigSaysMaintenance_ButHealthMonitorOnline_Collects()
    {
        var device = MakeDevice("PLC", status: DeviceStatus.Maintenance);
        _manager.Devices.Add(device);
        _monitor.Statuses[device.Id] = DeviceStatus.Online;

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectOnceAsync(CancellationToken.None);

        var read = Assert.Single(_reader.ReadDevices);
        Assert.Equal(device.Id, read.Id);
    }

    /// <summary>设备未注册进 HealthMonitor（历史数据）时，回退配置中的 Status</summary>
    [Fact]
    public async Task CollectOnceAsync_NoHealthSnapshot_FallsBackToConfigStatus()
    {
        var maintenanceDevice = MakeDevice("M", status: DeviceStatus.Maintenance);
        var onlineDevice = MakeDevice("O", status: DeviceStatus.Online);
        _manager.Devices.Add(maintenanceDevice);
        _manager.Devices.Add(onlineDevice);

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectOnceAsync(CancellationToken.None);

        var read = Assert.Single(_reader.ReadDevices);
        Assert.Equal(onlineDevice.Id, read.Id);
    }

    /// <summary>ADR-009 P1-1/P1-2：每轮采集刷新 devices_online 并上报整轮耗时（哑火指标接线回归）</summary>
    [Fact]
    public async Task CollectOnceAsync_ReportsOnlineAndDurationMetrics()
    {
        var device = MakeDevice("PLC");
        _manager.Devices.Add(device);
        _monitor.Statuses[device.Id] = DeviceStatus.Online;

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectOnceAsync(CancellationToken.None);

        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var exported = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("nitro_devices_online", exported);
        Assert.Contains("nitro_collection_duration_ms_count", exported);
    }

    // ── Helpers ──

    private static Device MakeDevice(string name, DeviceStatus status = DeviceStatus.Online) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" },
        Status = status
    };

    private sealed class FakeDeviceManager : IDeviceManager
    {
        public List<Device> Devices { get; } = [];

        public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(Devices.ToList()));
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
            => GetAllAsync(ct);
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
            => GetAllIncludingDeletedAsync(ct);
        public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeDeviceReader : IDeviceReader
    {
        public List<Device> ReadDevices { get; } = [];

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(Device device, CancellationToken ct)
        {
            ReadDevices.Add(device);
            return Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        }
    }

    private sealed class FakePipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class FakeDispatcher : IDataDispatcher
    {
        public Task<OperationResult> DispatchAsync(Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class FakeReporter : IHealthReporter
    {
        public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage) { }
    }

    private sealed class FakeHealthMonitor : IDeviceHealthMonitor
    {
        public Dictionary<Guid, DeviceStatus> Statuses { get; } = [];

        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId)
            => Statuses.TryGetValue(deviceId, out var s)
                ? new DeviceHealthSnapshot { DeviceId = deviceId, Status = s }
                : null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots()
            => Statuses.Select(kv => new DeviceHealthSnapshot { DeviceId = kv.Key, Status = kv.Value }).ToList();
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) => Statuses[deviceId] = status;
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public void Remove(Guid deviceId) => Statuses.Remove(deviceId);
        public void AddListener(IDeviceHealthListener listener) { }
    }
}

