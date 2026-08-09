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
/// ADR-016 P3-2/P3-3：探测名额闭环（异常路径也要 RecordFailure 关闭探测）+ 失败明细透传。
/// </summary>
public class DeviceCollectorProbeTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Device Device = new()
    {
        Id = DeviceId,
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
    };

    private static ServiceProvider BuildProvider(
        IDeviceReader reader, IPointValuePipeline pipeline, IHealthReporter reporter)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:IntervalMs"] = "1000",
                ["Collection:MaxConcurrency"] = "1",
                ["Collection:CircuitBreakerOpenSeconds"] = "0", // 冷却 0 → Trip 后立即可进入 HalfOpen 探测
                ["Collection:CircuitBreakerMaxOpenSeconds"] = "10"
            })
            .Build());
        services.AddSingleton<IDeviceManager>(new EmptyDeviceManager());
        services.AddSingleton<IDeviceReader>(reader);
        services.AddSingleton<IPointValuePipeline>(pipeline);
        services.AddSingleton<IDataDispatcher>(new NoopDispatcher());
        services.AddSingleton<IHealthReporter>(reporter);
        services.AddSingleton<IDeviceHealthMonitor>(new FakeHealthMonitor());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CollectDeviceAsync_WhenReaderThrows_ClosesProbeByRecordFailure()
    {
        await using var provider = BuildProvider(new ThrowingReader(), new EmptyPipeline(), new CapturingReporter());
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
        var breaker = provider.GetRequiredService<ICircuitBreakerRegistry>().Get(DeviceId);

        breaker.Trip(); // Open → 冷却 0 → 下次 TryEnterProbe 进入 HalfOpen 并抢占探测

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => collector.CollectDeviceAsync(Device, CancellationToken.None));

        // P3-2 回归：异常路径必须 RecordFailure → 状态回到 Open；
        // 若未闭环，状态会停留在 HalfOpen 且探测名额被占 30s。
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task CollectDeviceAsync_ReportsFirstBadSnapshotErrorMessage()
    {
        var reporter = new CapturingReporter();
        var pipeline = new FixedPipeline([
            new PointSnapshot
            {
                DeviceId = DeviceId,
                DevicePointId = Guid.NewGuid(),
                DataType = DataType.Float,
                Quality = QualityCode.Uncertain,
                ErrorMessage = "缩放失败：无法转换为数值"
            }
        ]);

        await using var provider = BuildProvider(new SuccessReader(), pipeline, reporter);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(Device, CancellationToken.None);

        // P3-3 回归：失败明细不再被 null 吞掉（HealthMonitor.LastError 可见真实原因）
        Assert.Equal("缩放失败：无法转换为数值", reporter.LastErrorMessage);
    }

    // ── Fakes ──

    private sealed class ThrowingReader : IDeviceReader
    {
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
            Device device, CancellationToken ct)
            => throw new InvalidOperationException("reader boom");
    }

    private sealed class SuccessReader : IDeviceReader
    {
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
            Device device, CancellationToken ct)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(
                [new RawPointValue { Point = MakePoint(), Value = 1, Timestamp = DateTime.UtcNow }]));

        private static DevicePoint MakePoint() => new()
        {
            Id = Guid.NewGuid(),
            Name = "p1",
            Address = "40001",
            DataType = DataType.Float,
            ScaleFactor = 1.0
        };
    }

    private sealed class EmptyPipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class FixedPipeline : IPointValuePipeline
    {
        private readonly IReadOnlyList<PointSnapshot> _snapshots;
        public FixedPipeline(IReadOnlyList<PointSnapshot> snapshots) => _snapshots = snapshots;
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => _snapshots;
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class NoopDispatcher : IDataDispatcher
    {
        public Task<OperationResult> DispatchAsync(
            Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class CapturingReporter : IHealthReporter
    {
        public string? LastErrorMessage { get; private set; }
        public void Report(Guid deviceId, int successCount, int failCount, string? errorMessage)
            => LastErrorMessage = errorMessage;
    }

    private sealed class EmptyDeviceManager : IDeviceManager
    {
        public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success([]));
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeHealthMonitor : IDeviceHealthMonitor
    {
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void ReportSuccess(Guid deviceId) { }
        public void ReportFailure(Guid deviceId, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) { }
    }
}