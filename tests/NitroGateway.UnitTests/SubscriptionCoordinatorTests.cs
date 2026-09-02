using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-071：SubscriptionCoordinator 尽力激活契约 + DeviceCollector 接入行为。
/// 覆盖：
/// - 驱动池缺失 / 无 enabled 点 / 驱动不支持订阅 / 非 ISubscriptionSource → 返回 false（回退轮询）；
/// - Ensure 成功且 IsSubscriptionActive → 返回 true；
/// - Ensure 失败或成功但未激活 → 返回 false 且解绑通知事件；
/// - 通知到达 → 复用 Pipeline.Process → Dispatcher.DispatchAsync → HealthReporter.Report（唯一管道）；
/// - AC-4：TryActivateAsync=true 时 DeviceCollector 跳过轮询 Reader；false 时继续轮询。
/// </summary>
public class SubscriptionCoordinatorTests
{
    private static readonly Device Device = new()
    {
        Id = Guid.NewGuid(),
        Name = "OPC-1",
        Protocol = new ProtocolIdentifier { Name = "OPCUA", Dialect = "UA" },
        Connection = new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840" }
    };

    // ── SubscriptionCoordinator 单元测试 ──

    /// <summary>驱动池未注入（null，未注册协议层的宿主）→ 恒返回 false，采集保持轮询兜底。</summary>
    [Fact]
    public async Task TryActivateAsync_NullDriverPool_ReturnsFalse()
    {
        var coordinator = CreateCoordinator(
            driverPool: null,
            pipeline: new FakePipeline(),
            dispatcher: new FakeDispatcher(),
            reporter: new FakeReporter());

        var activated = await coordinator.TryActivateAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.False(activated);
    }

    /// <summary>无 enabled 点位 → 返回 false，且不触碰驱动池。</summary>
    [Fact]
    public async Task TryActivateAsync_NoEnabledPoints_ReturnsFalse_WithoutTouchingPool()
    {
        var driver = new FakeSubscriptionDriver();
        var pool = new FakeDriverPool { Driver = driver };
        var coordinator = CreateCoordinator(pool, new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var device = DeviceWithPoints();
        foreach (var point in device.Points.ToList())
            point.Enabled = false;

        var activated = await coordinator.TryActivateAsync(device, CancellationToken.None);

        Assert.False(activated);
        Assert.Equal(0, pool.GetOrCreateCount);
    }

    /// <summary>驱动不支持订阅（Capability.SupportsSubscription=false，如 Modbus/S7）→ 返回 false，回退轮询。</summary>
    [Fact]
    public async Task TryActivateAsync_DriverWithoutSubscriptionCapability_ReturnsFalse()
    {
        var driver = new FakeNonSubscriptionDriver { SupportsSubscription = false };
        var coordinator = CreateCoordinator(
            new FakeDriverPool { Driver = driver },
            new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var activated = await coordinator.TryActivateAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.False(activated);
    }

    /// <summary>驱动声明支持订阅但未实现 ISubscriptionSource → 返回 false（尽力激活降级）。</summary>
    [Fact]
    public async Task TryActivateAsync_NotSubscriptionSource_ReturnsFalse()
    {
        var driver = new FakeNonSubscriptionDriver { SupportsSubscription = true };
        var coordinator = CreateCoordinator(
            new FakeDriverPool { Driver = driver },
            new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var activated = await coordinator.TryActivateAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.False(activated);
    }

    /// <summary>Ensure 成功且订阅激活 → 返回 true；仅传 enabled 点；发布间隔取全局采集间隔。</summary>
    [Fact]
    public async Task TryActivateAsync_EnsureSucceedsAndActive_ReturnsTrue_WithEnabledPointsAndInterval()
    {
        var driver = new FakeSubscriptionDriver { IsSubscriptionActive = true };
        var pool = new FakeDriverPool { Driver = driver };
        var coordinator = CreateCoordinator(pool, new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var device = DeviceWithPoints();
        device.Points.Last().Enabled = false; // 禁用最后一个点，协调器只应传 enabled 点

        var activated = await coordinator.TryActivateAsync(device, CancellationToken.None);

        Assert.True(activated);
        Assert.Equal(1, driver.EnsureCallCount);
        Assert.NotNull(driver.LastEnsurePoints);
        Assert.Equal(1, driver.LastEnsurePoints!.Count);
        Assert.Equal(device.Points.First(), driver.LastEnsurePoints![0]);
        Assert.Equal(1000, driver.LastPublishingIntervalMs); // Options IntervalMs = 1000
        Assert.Equal(1, driver.HandlerCount); // 成功后事件保持绑定
    }

    /// <summary>Ensure 失败 → 返回 false 并解绑通知事件（可恢复的能力降级，不静默停采）。</summary>
    [Fact]
    public async Task TryActivateAsync_EnsureFails_ReturnsFalse_AndUnsubscribes()
    {
        var driver = new FakeSubscriptionDriver
        {
            EnsureResult = OperationalError.Protocol("订阅创建失败")
        };
        var coordinator = CreateCoordinator(
            new FakeDriverPool { Driver = driver },
            new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var activated = await coordinator.TryActivateAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.False(activated);
        Assert.Equal(0, driver.HandlerCount); // 失败路径解绑事件
    }

    /// <summary>Ensure 成功但订阅未激活（IsSubscriptionActive=false）→ 返回 false 并解绑事件。</summary>
    [Fact]
    public async Task TryActivateAsync_EnsureSucceedsButNotActive_ReturnsFalse_AndUnsubscribes()
    {
        var driver = new FakeSubscriptionDriver
        {
            IsSubscriptionActive = false // Ensure 成功但状态未激活
        };
        var coordinator = CreateCoordinator(
            new FakeDriverPool { Driver = driver },
            new FakePipeline(), new FakeDispatcher(), new FakeReporter());

        var activated = await coordinator.TryActivateAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.False(activated);
        Assert.Equal(0, driver.HandlerCount);
    }

    /// <summary>AC-3：订阅通知 → 复用 Pipeline.Process → Dispatcher.DispatchAsync → Reporter.Report（唯一管道）。</summary>
    [Fact]
    public async Task ValuesReceived_DispatchesThroughPipelineAndDispatcherAndReports()
    {
        var driver = new FakeSubscriptionDriver { IsSubscriptionActive = true };
        var pipeline = new FakePipeline();
        var dispatcher = new FakeDispatcher();
        var reporter = new FakeReporter();
        var coordinator = CreateCoordinator(new FakeDriverPool { Driver = driver }, pipeline, dispatcher, reporter);

        var device = DeviceWithPoints();
        Assert.True(await coordinator.TryActivateAsync(device, CancellationToken.None));

        var point = device.Points.First();
        var raw = new RawPointValue { Point = point, Value = 1.5, Timestamp = DateTime.UtcNow };
        await driver.PublishAsync([raw]); // 模拟驱动推送一批 Good 原始值

        var process = Assert.Single(pipeline.ProcessCalls);
        Assert.Equal(device.Id, process.DeviceId);
        Assert.Same(raw, Assert.Single(process.Raw));

        var dispatch = Assert.Single(dispatcher.DispatchCalls);
        Assert.Equal(device.Id, dispatch.DeviceId);
        var snapshot = Assert.Single(dispatch.Snapshots);
        Assert.Equal(point.Id, snapshot.DevicePointId);
        Assert.Equal(point.Name, snapshot.PointName);
        Assert.Equal(1.5, snapshot.Value);

        Assert.Equal(1, reporter.ReportCount);
        Assert.True(reporter.LastSucceeded);
        Assert.Equal(device.Id, reporter.LastDeviceId);
    }

    /// <summary>空快照（Pipeline 抑制后）→ 不调 Dispatcher，仍上报健康成功。</summary>
    [Fact]
    public async Task ValuesReceived_EmptySnapshots_SkipsDispatcherButReports()
    {
        var driver = new FakeSubscriptionDriver { IsSubscriptionActive = true };
        var pipeline = new EmptyResultPipeline(); // 模拟死区抑制全灭
        var dispatcher = new FakeDispatcher();
        var reporter = new FakeReporter();
        var coordinator = CreateCoordinator(new FakeDriverPool { Driver = driver }, pipeline, dispatcher, reporter);

        var device = DeviceWithPoints();
        Assert.True(await coordinator.TryActivateAsync(device, CancellationToken.None));

        await driver.PublishAsync([new RawPointValue
        {
            Point = device.Points.First(),
            Value = 1.5,
            Timestamp = DateTime.UtcNow
        }]);

        Assert.Empty(dispatcher.DispatchCalls);
        Assert.Equal(1, reporter.ReportCount); // 订阅链路健康，不因死区抑制而误报
    }

    // ── AC-4：DeviceCollector 接入行为（经 DI，SubscriptionCoordinator 为可配置 fake）──

    /// <summary>TryActivateAsync=true → 订阅接管，DeviceCollector 直接 return，不调轮询 Reader。</summary>
    [Fact]
    public async Task CollectDeviceAsync_SubscriptionActivated_SkipsPollingReader()
    {
        var reader = new FakeCollectorReader { DueResult = [EnabledPoint()] };
        var coordinator = new FakeSubscriptionCoordinator { Result = true };

        await using var provider = BuildCollectorProvider(reader, coordinator);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.Equal(1, coordinator.CallCount);
        Assert.Equal(0, reader.ReadCallCount); // 订阅生效 → 轮询 Reader 一次都不调
    }

    /// <summary>TryActivateAsync=false → 协调器回退，DeviceCollector 继续原轮询路径。</summary>
    [Fact]
    public async Task CollectDeviceAsync_SubscriptionNotActivated_StillPolls()
    {
        var reader = new FakeCollectorReader
        {
            DueResult = [EnabledPoint()],
            ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Success([])
        };
        var coordinator = new FakeSubscriptionCoordinator { Result = false };

        await using var provider = BuildCollectorProvider(reader, coordinator);
        using var scope = provider.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();

        await collector.CollectDeviceAsync(DeviceWithPoints(), CancellationToken.None);

        Assert.Equal(1, coordinator.CallCount);
        Assert.Equal(1, reader.ReadCallCount); // 订阅不可用 → 保持 v1 轮询兜底
    }

    // ── Helpers ──

    private static Device DeviceWithPoints()
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = Device.Name,
            Protocol = Device.Protocol,
            Connection = Device.Connection
        };
        device.AddPoint(EnabledPoint());
        device.AddPoint(EnabledPoint("p2"));
        return device;
    }

    private static DevicePoint EnabledPoint(string name = "p1") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = name == "p2" ? "ns=2;i=1002" : "ns=2;i=1001",
        DataType = DataType.Float,
        Enabled = true
    };

    private static SubscriptionCoordinator CreateCoordinator(
        IProtocolDriverPool? driverPool,
        IPointValuePipeline pipeline,
        IDataDispatcher dispatcher,
        IHealthReporter reporter) => new(
        driverPool,
        pipeline,
        dispatcher,
        reporter,
        Options.Create(new CollectionOption { IntervalMs = 1000 }),
        NullLogger<SubscriptionCoordinator>.Instance);

    /// <summary>复用 DeviceCollectorScanIntervalTests 的 DI 装配范式：后注册覆盖协调器，用于断言订阅接管行为。</summary>
    private static ServiceProvider BuildCollectorProvider(
        FakeCollectorReader reader, FakeSubscriptionCoordinator coordinator)
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
        services.AddSingleton<IPointValuePipeline>(new EmptyCollectorPipeline());
        services.AddSingleton<IDataDispatcher>(new EmptyCollectorDispatcher());
        services.AddSingleton<IHealthReporter>(new EmptyCollectorReporter());
        services.AddSingleton<IDeviceHealthMonitor>(new EmptyCollectorHealthMonitor());
        services.AddSingleton<IDeviceManager>(new EmptyCollectorDeviceManager());
        services.AddSingleton<ICircuitBreakerRegistry>(new EmptyCollectorBreakers());
        // 后注册覆盖 AddNitroCollection 自建的 SubscriptionCoordinator（MS.DI 取最后一个），注入可控 fake
        services.AddSingleton<ISubscriptionCoordinator>(coordinator);
        return services.BuildServiceProvider();
    }

    // ── Fakes：SubscriptionCoordinator ──

    private sealed class FakeDriverPool : IProtocolDriverPool
    {
        public IProtocolDriver Driver { get; init; } = new FakeSubscriptionDriver();
        public int GetOrCreateCount { get; private set; }

        public IProtocolDriver GetOrCreate(Device device)
        {
            GetOrCreateCount++;
            return Driver;
        }

        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    /// <summary>支持订阅的驱动：可编程 Ensure 结果 / 激活状态，记录订阅调用并暴露事件发布入口。</summary>
    private sealed class FakeSubscriptionDriver : IProtocolDriver, ISubscriptionSource
    {
        public DriverState State { get; set; } = DriverState.Connected;
        public DriverCapability Capability { get; init; } = new() { SupportsSubscription = true };
        public bool IsSubscriptionActive { get; set; }
        public OperationResult EnsureResult { get; set; } = OperationResult.Success();
        public int EnsureCallCount { get; private set; }
        public IReadOnlyList<DevicePoint>? LastEnsurePoints { get; private set; }
        public int? LastPublishingIntervalMs { get; private set; }
        public int StopCallCount { get; private set; }
        public int HandlerCount => ValuesReceived?.GetInvocationList().Length ?? 0;

        public event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived;

        public Task PublishAsync(IReadOnlyList<RawPointValue> values)
        {
            var handler = ValuesReceived;
            return handler is null ? Task.CompletedTask : handler(values);
        }

        public Task<OperationResult> EnsureSubscriptionAsync(
            IReadOnlyList<DevicePoint> points, int publishingIntervalMs, CancellationToken ct = default)
        {
            EnsureCallCount++;
            LastEnsurePoints = points;
            LastPublishingIntervalMs = publishingIntervalMs;
            return Task.FromResult(EnsureResult);
        }

        public Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default)
        {
            StopCallCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("订阅接管，不轮询")));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(
                OperationalError.Protocol("订阅接管，不轮询"));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }

    /// <summary>声明（或未声明）订阅能力但不实现 ISubscriptionSource 的驱动。</summary>
    private sealed class FakeNonSubscriptionDriver : IProtocolDriver
    {
        public bool SupportsSubscription { get; init; }
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new() { SupportsSubscription = SupportsSubscription };
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("不支持")));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(
                OperationalError.Protocol("不支持"));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }

    private sealed class FakePipeline : IPointValuePipeline
    {
        public List<(Guid DeviceId, IReadOnlyList<RawPointValue> Raw)> ProcessCalls { get; } = [];

        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues)
        {
            ProcessCalls.Add((deviceId, rawValues));
            return rawValues.Select(raw => new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = raw.Point.Id,
                PointName = raw.Point.Name,
                DataType = raw.Point.DataType,
                RawValue = raw.Value,
                Value = raw.Value,
                Timestamp = raw.Timestamp
            }).ToList();
        }

        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class EmptyResultPipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class FakeDispatcher : IDataDispatcher
    {
        public List<(Guid DeviceId, IReadOnlyList<PointSnapshot> Snapshots)> DispatchCalls { get; } = [];

        public Task<OperationResult> DispatchAsync(
            Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
        {
            DispatchCalls.Add((deviceId, snapshots));
            return Task.FromResult(OperationResult.Success());
        }
    }

    private sealed class FakeReporter : IHealthReporter
    {
        public int ReportCount { get; private set; }
        public Guid? LastDeviceId { get; private set; }
        public bool? LastSucceeded { get; private set; }

        public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage)
        {
            ReportCount++;
            LastDeviceId = deviceId;
            LastSucceeded = succeeded;
        }
    }

    // ── Fakes：DeviceCollector（AC-4）──

    private sealed class FakeSubscriptionCoordinator : ISubscriptionCoordinator
    {
        public bool Result { get; init; }
        public int CallCount { get; private set; }

        public Task<bool> TryActivateAsync(Device device, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCollectorReader : IDeviceReader
    {
        public IReadOnlyList<DevicePoint>? DueResult { get; init; }
        public OperationResult<IReadOnlyList<RawPointValue>> ReadResult { get; init; } =
            OperationResult<IReadOnlyList<RawPointValue>>.Success([]);
        public int ReadCallCount { get; private set; }

        public IReadOnlyList<DevicePoint>? GetDuePoints(Device device) => DueResult;

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(Device device, CancellationToken ct)
        {
            ReadCallCount++;
            return Task.FromResult(ReadResult);
        }
    }

    private sealed class EmptyCollectorPipeline : IPointValuePipeline
    {
        public IReadOnlyList<PointSnapshot> Process(Guid deviceId, IReadOnlyList<RawPointValue> rawValues) => [];
        public double? GetLastValue(Guid pointId) => null;
        public void SetLastValue(Guid pointId, double value) { }
    }

    private sealed class EmptyCollectorDispatcher : IDataDispatcher
    {
        public Task<OperationResult> DispatchAsync(
            Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class EmptyCollectorReporter : IHealthReporter
    {
        public void Report(Guid deviceId, string? deviceName, bool succeeded, string? errorMessage) { }
    }

    private sealed class EmptyCollectorHealthMonitor : IDeviceHealthMonitor
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

    private sealed class EmptyCollectorDeviceManager : IDeviceManager
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

    private sealed class EmptyCollectorBreakers : ICircuitBreakerRegistry
    {
        private readonly CircuitBreakerRegistry _inner = new(TimeSpan.Zero, TimeSpan.FromSeconds(10));

        public ICircuitBreaker Get(Guid deviceId) => _inner.Get(deviceId);
        public void Reset(Guid deviceId) => _inner.Reset(deviceId);
        public IReadOnlyDictionary<Guid, ICircuitBreaker> GetAll() => _inner.GetAll();
    }
}
