using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-062：点位级 ScanIntervalMs 降频采样——DeviceCollector 在熔断检查前先做到期判定，
/// 全部未到期 → 跳过本轮：不调驱动、不触发熔断（TryEnterProbe/RecordSuccess/RecordFailure 全不碰）、
/// 不更新健康快照（保持上次状态，既不误报在线也不误判离线）。
/// </summary>
public class DeviceCollectorScanIntervalTests
{
    private static readonly Device Device = new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
    };

    private static ServiceProvider BuildProvider(
        FakeReader reader, CapturingReporter reporter, SpyBreakerRegistry breakers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:IntervalMs"] = "1000",
                ["Collection:MaxConcurrency"] = "1",
                ["Collection:CircuitBreakerOpenSeconds"] = "0",
                ["Collection:CircuitBreakerMaxOpenSeconds"] = "10"
            })
            .Build());
        services.AddSingleton<IDeviceReader>(reader);
        services.AddSingleton<IPointValuePipeline>(new EmptyPipeline());
        services.AddSingleton<IDataDispatcher>(new NoopDispatcher());
        services.AddSingleton<IHealthReporter>(reporter);
        services.AddSingleton<IDeviceHealthMonitor>(new FakeHealthMonitor());
        services.AddSingleton<IDeviceManager>(new EmptyDeviceManager());
        // 后注册覆盖 AddNitroCollection 自建的注册表（MS.DI 取最后一个），用于断言跳过轮不触碰熔断器
        services.AddSingleton<ICircuitBreakerRegistry>(breakers);
        return services.BuildServiceProvider();
    }

    /// <summary>全部未到期 → 跳过：不调驱动、不触发熔断、不更新健康快照。</summary>
    [Fact]
    public async Task CollectDeviceAsync_AllPointsNotDue_SkipsWithoutProbeOrHealth()
    {
        var reader = new FakeReader { DueResult = [] }; // 空 = 全部未到期
        var reporter = new CapturingReporter();
        var breakers = new SpyBreakerRegistry();

        await using var provider = BuildProvider(reader, reporter, breakers);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(Device, CancellationToken.None);

        Assert.Equal(0, reader.ReadCallCount);
        Assert.False(reporter.Called); // 跳过轮不得更新健康快照
        Assert.Equal(0, breakers.GetCallCount); // 跳过轮不得触发熔断（Get/TryEnterProbe 都不碰）
        Assert.Empty(breakers.Registry.GetAll()); // 跳过轮不得为设备惰性创建熔断器
    }

    /// <summary>有到期点位 → 正常采集（读 + 上报 + 熔断成功）。</summary>
    [Fact]
    public async Task CollectDeviceAsync_HasDuePoints_ReadsAndReports()
    {
        var reader = new FakeReader
        {
            DueResult = [new DevicePoint { Id = Guid.NewGuid(), Name = "p1", Address = "40001", Enabled = true }],
            ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Success([])
        };
        var reporter = new CapturingReporter();
        var breakers = new SpyBreakerRegistry();

        await using var provider = BuildProvider(reader, reporter, breakers);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(Device, CancellationToken.None);

        Assert.Equal(1, reader.ReadCallCount);
        Assert.True(reporter.Called);
        Assert.True(reporter.LastSucceeded, "读取成功（含空列表）→ 上报成功，推进探测");
        Assert.Equal(1, breakers.GetCallCount);
        Assert.Equal(CircuitState.Closed, breakers.Registry.Get(Device.Id).State);
    }

    /// <summary>无 enabled 点位（GetDuePoints=null）→ 仍走 ADR-031 探活：ReadDeviceAsync 被调用。</summary>
    [Fact]
    public async Task CollectDeviceAsync_NoEnabledPoints_StillProbes()
    {
        var reader = new FakeReader { DueResult = null }; // null = 无 enabled 点
        var reporter = new CapturingReporter();
        var breakers = new SpyBreakerRegistry();

        await using var provider = BuildProvider(reader, reporter, breakers);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(Device, CancellationToken.None);

        Assert.Equal(1, reader.ReadCallCount); // 空点位设备仍走真实探活（ADR-031 回归）
        Assert.True(reporter.Called);
    }

    // ── Fakes ──

    private sealed class FakeReader : IDeviceReader
    {
        /// <summary>null=无 enabled 点（探活）；空=全部未到期（跳过）；非空=到期子集。</summary>
        public IReadOnlyList<DevicePoint>? DueResult { get; init; } = [];
        public OperationResult<IReadOnlyList<RawPointValue>> ReadResult { get; init; } =
            OperationResult<IReadOnlyList<RawPointValue>>.Success([]);
        public int ReadCallCount { get; private set; }

        public IReadOnlyList<DevicePoint>? GetDuePoints(Device device) => DueResult;

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
            Device device, CancellationToken ct)
        {
            ReadCallCount++;
            return Task.FromResult(ReadResult);
        }
    }

    /// <summary>包装真实注册表并统计 Get 调用次数，用于断言跳过轮不触碰熔断。</summary>
    private sealed class SpyBreakerRegistry : ICircuitBreakerRegistry
    {
        public CircuitBreakerRegistry Registry { get; } = new(TimeSpan.Zero, TimeSpan.FromSeconds(10));
        public int GetCallCount { get; private set; }

        public ICircuitBreaker Get(Guid deviceId)
        {
            GetCallCount++;
            return Registry.Get(deviceId);
        }

        public void Reset(Guid deviceId) => Registry.Reset(deviceId);
        public IReadOnlyDictionary<Guid, ICircuitBreaker> GetAll() => Registry.GetAll();
    }

    private sealed class CapturingReporter : IHealthReporter
    {
        public bool Called { get; private set; }
        public bool? LastSucceeded { get; private set; }

        public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage)
        {
            Called = true;
            LastSucceeded = succeeded;
        }
    }

    private sealed class EmptyPipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class NoopDispatcher : IDataDispatcher
    {
        public Task<OperationResult> DispatchAsync(
            Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class EmptyDeviceManager : IDeviceManager
    {
        public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success([]));
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default) => GetAllAsync(ct);
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default) => GetAllIncludingDeletedAsync(ct);
        public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeHealthMonitor : IDeviceHealthMonitor
    {
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) { }
    }
}
