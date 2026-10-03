using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

/// <summary>
/// DeviceCollector 失败/熔断路径（变异覆盖补齐）：
/// reader 返回失败（非抛异常）→ 上报失败 + 闭合探测 + 不分发；
/// 熔断 Open → 跳过且不触达 reader/dispatcher/reporter。
/// </summary>
public class DeviceCollectorFailureTests
{
    private static readonly Guid DeviceId = Guid.NewGuid();

    private static Device MakeDevice() => new()
    {
        Id = DeviceId,
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
    };

    private static ServiceProvider BuildProvider(
        IDeviceReader reader, IDataDispatcher dispatcher, IHealthReporter reporter,
        int openSeconds, int maxOpenSeconds,
        IPointValuePipeline? pipeline = null, IDeviceManager? manager = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:IntervalMs"] = "1000",
                ["Collection:MaxConcurrency"] = "1",
                ["Collection:CircuitBreakerOpenSeconds"] = openSeconds.ToString(),
                ["Collection:CircuitBreakerMaxOpenSeconds"] = maxOpenSeconds.ToString()
            })
            .Build());
        services.AddSingleton(manager ?? new FakeDeviceManager());
        services.AddSingleton<IDeviceReader>(reader);
        services.AddSingleton<IPointValuePipeline>(pipeline ?? new EmptyPipeline());
        services.AddSingleton<IDataDispatcher>(dispatcher);
        services.AddSingleton<IHealthReporter>(reporter);
        services.AddSingleton<IDeviceHealthMonitor>(new FakeHealthMonitor());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CollectDeviceAsync_ReaderReturnsFailure_ReportsFailNoDispatchClosesProbe()
    {
        var reader = new DueReader(OperationResult<IReadOnlyList<RawPointValue>>.Failure(
            OperationalError.Communication("从站无响应")));
        var dispatcher = new CapturingDispatcher();
        var reporter = new CapturingReporter();

        await using var provider = BuildProvider(reader, dispatcher, reporter, openSeconds: 0, maxOpenSeconds: 10);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
        var breaker = provider.GetRequiredService<ICircuitBreakerRegistry>().Get(DeviceId);

        breaker.Trip(); // 冷却 0 → 下次 TryEnterProbe 进入 HalfOpen 并抢占探测

        await collector.CollectDeviceAsync(MakeDevice(), CancellationToken.None);

        Assert.False(reporter.LastSucceeded, "读取失败必须上报失败供健康监控判离线");
        Assert.False(dispatcher.Called, "读取失败不得分发任何数据");
        Assert.Equal(CircuitState.Open, breaker.State); // 探测失败 → 闭合判定，回到 Open
    }

    [Fact]
    public async Task CollectDeviceAsync_CircuitOpen_SkipsWithoutTouchingReaderDispatcherReporter()
    {
        var reader = new DueReader(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        var dispatcher = new CapturingDispatcher();
        var reporter = new CapturingReporter();

        await using var provider = BuildProvider(reader, dispatcher, reporter, openSeconds: 30, maxOpenSeconds: 300);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
        var breaker = provider.GetRequiredService<ICircuitBreakerRegistry>().Get(DeviceId);

        breaker.Trip(); // Open，冷却 30s → TryEnterProbe 返回 false

        await collector.CollectDeviceAsync(MakeDevice(), CancellationToken.None);

        Assert.Equal(0, reader.ReadCalls);
        Assert.False(dispatcher.Called);
        Assert.False(reporter.Reported);
    }

    [Fact]
    public async Task CollectDeviceAsync_SuccessButZeroSnapshots_SkipsDispatchButReportsSuccess()
    {
        var reader = new DueReader(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        var dispatcher = new CapturingDispatcher();
        var reporter = new CapturingReporter();

        await using var provider = BuildProvider(reader, dispatcher, reporter, openSeconds: 0, maxOpenSeconds: 10);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(MakeDevice(), CancellationToken.None);

        Assert.False(dispatcher.Called, "0 快照不得分发");
        Assert.True(reporter.LastSucceeded, "读取成功即成功，与快照数无关");
    }

    [Fact]
    public async Task CollectDeviceAsync_SuccessWithSnapshots_DispatchesToDispatcher()
    {
        var snapshot = new PointSnapshot
        {
            DeviceId = DeviceId,
            DevicePointId = Guid.NewGuid(),
            PointName = "P1",
            DataType = DataType.Float,
            Value = 1.0,
            Timestamp = DateTime.UtcNow,
            Quality = QualityCode.Good
        };
        var reader = new DueReader(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        var dispatcher = new CapturingDispatcher();
        var reporter = new CapturingReporter();

        await using var provider = BuildProvider(
            reader, dispatcher, reporter, openSeconds: 0, maxOpenSeconds: 10,
            pipeline: new FixedPipeline([snapshot]));
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(MakeDevice(), CancellationToken.None);

        Assert.True(dispatcher.Called);
        Assert.Same(snapshot, Assert.Single(dispatcher.LastSnapshots));
    }

    [Fact]
    public async Task CollectDeviceAsync_HalfOpenProbeSucceeds_RecordsSuccessAndReturnsToClosed()
    {
        var reader = new DueReader(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        var dispatcher = new CapturingDispatcher();
        var reporter = new CapturingReporter();

        await using var provider = BuildProvider(reader, dispatcher, reporter, openSeconds: 0, maxOpenSeconds: 10);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
        var breaker = provider.GetRequiredService<ICircuitBreakerRegistry>().Get(DeviceId);

        breaker.Trip(); // 冷却 0 → TryEnterProbe 进入 HalfOpen 并占探测名额

        await collector.CollectDeviceAsync(MakeDevice(), CancellationToken.None);

        Assert.Equal(CircuitState.Closed, breaker.State); // 探测成功必须 RecordSuccess → Closed
    }

    // ── Fakes ──

    private sealed class DueReader : IDeviceReader
    {
        private readonly OperationResult<IReadOnlyList<RawPointValue>> _result;
        public DueReader(OperationResult<IReadOnlyList<RawPointValue>> result) => _result = result;

        private int _readCalls;
        public int ReadCalls => Volatile.Read(ref _readCalls);

        public IReadOnlyList<DevicePoint>? GetDuePoints(Device device) =>
        [
            new DevicePoint
            {
                Id = Guid.NewGuid(), Name = "p1", Address = "40001",
                DataType = DataType.Float, Enabled = true
            }
        ];

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
            Device device, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCalls);
            return Task.FromResult(_result);
        }
    }

    private sealed class EmptyPipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class FixedPipeline(IReadOnlyList<PointSnapshot> snapshots) : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => snapshots;
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class CapturingDispatcher : IDataDispatcher
    {
        public volatile bool Called;
        public IReadOnlyList<PointSnapshot> LastSnapshots { get; private set; } = [];
        public Task<OperationResult> DispatchAsync(
            Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
        {
            Called = true;
            LastSnapshots = snapshots;
            return Task.FromResult(OperationResult.Success());
        }
    }

    private sealed class CapturingReporter : IHealthReporter
    {
        public volatile bool Reported;
        public bool? LastSucceeded;
        public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage)
        {
            Reported = true;
            LastSucceeded = succeeded;
        }
    }
}
