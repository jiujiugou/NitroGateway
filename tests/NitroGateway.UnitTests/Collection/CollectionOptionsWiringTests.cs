using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

public class CollectionOptionsWiringTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build());
        return services.BuildServiceProvider();
    }

    /// <summary>配置节的 4 个字段全部绑定进 IOptions&lt;CollectionOption&gt;</summary>
    [Fact]
    public void AddNitroCollection_BindsAllOptionFields()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:IntervalMs"] = "123",
            ["Collection:MaxConcurrency"] = "7",
            ["Collection:CircuitBreakerOpenSeconds"] = "9",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "1234"
        });

        var options = provider.GetRequiredService<IOptions<CollectionOption>>().Value;

        Assert.Equal(123, options.IntervalMs);
        Assert.Equal(7, options.MaxConcurrency);
        Assert.Equal(9, options.CircuitBreakerOpenSeconds);
        Assert.Equal(1234, options.CircuitBreakerMaxOpenSeconds);
    }

    /// <summary>
    /// 熔断器冷却时长来自配置：CircuitBreakerOpenSeconds=0 时 Trip 后立即进入半开（TryEnterProbe=true）；
    /// 若仍走硬编码 5s 默认值，Trip 后应保持 Open（TryEnterProbe=false），此用例即红绿对照。
    /// </summary>
    [Fact]
    public void AddNitroCollection_CircuitBreakerOpenDurationComesFromConfig()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "0",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "10"
        });

        var registry = provider.GetRequiredService<ICircuitBreakerRegistry>();
        var breaker = registry.Get(Guid.NewGuid());

        breaker.Trip();

        Assert.True(breaker.TryEnterProbe());
    }

    /// <summary>无 Collection 配置节点时所有字段使用 CollectionOption 默认值，不抛异常</summary>
    [Fact]
    public void AddNitroCollection_MissingSection_UsesDefaults()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<IOptions<CollectionOption>>().Value;

        Assert.Equal(1000, options.IntervalMs);
        Assert.Equal(5, options.MaxConcurrency);
        Assert.Equal(5, options.CircuitBreakerOpenSeconds);
        Assert.Equal(300, options.CircuitBreakerMaxOpenSeconds);
    }

    [Theory]
    [InlineData("IntervalMs", "0")]
    [InlineData("IntervalMs", "-1")]
    [InlineData("MaxConcurrency", "0")]
    public void AddNitroCollection_InvalidIntervalOrConcurrency_Throws(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"Collection:{key}"] = value
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
    }

    [Fact]
    public void AddNitroCollection_MaxOpenLessThanOpen_Throws()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "10",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "5"
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
    }

    [Fact]
    public void AddNitroCollection_OpenEqualsMax_DoesNotThrow()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "10",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "10"
        });

        Assert.Equal(10, provider.GetRequiredService<IOptions<CollectionOption>>().Value.CircuitBreakerOpenSeconds);
    }

    [Fact]
    public void AddNitroCollection_NegativeCircuitBreakerOpen_ThrowsWithMessage()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "-1"
        });

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
        Assert.Contains("CircuitBreakerOpenSeconds", ex.Message);
    }

    [Fact]
    public void AddNitroCollection_ZeroHeartbeat_ThrowsWithMessage()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:DeadbandHeartbeatMs"] = "0"
        });

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
        Assert.Contains("DeadbandHeartbeatMs", ex.Message);
    }

    /// <summary>核心服务全部可从容器解析（DI 接线不许漏注册）。</summary>
    [Fact]
    public void AddNitroCollection_RegistersCoreServices_AllResolvable()
    {
        using var provider = BuildFullProvider();

        Assert.NotNull(provider.GetRequiredService<IDeviceReader>());
        Assert.NotNull(provider.GetRequiredService<IPointValuePipeline>());
        Assert.NotNull(provider.GetRequiredService<ChangeDetector>());
        Assert.NotNull(provider.GetRequiredService<ICircuitBreakerRegistry>());
        Assert.NotNull(provider.GetRequiredService<IHealthReporter>());
        Assert.NotNull(provider.GetRequiredService<MeasurementWriteHost>());
        Assert.NotNull(provider.GetRequiredService<SinkDispatcher>());
        Assert.NotNull(provider.GetRequiredService<ISubscriptionCoordinator>());
        Assert.NotNull(provider.GetRequiredService<IDataDispatcher>());

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeviceCollector>());
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("http")]
    [InlineData("both")]
    [InlineData("MQTT")]
    public void AddNitroCollection_ForwardChannels_ValidValuesResolve(string channels)
    {
        using var provider = BuildFullProvider(new Dictionary<string, string?>
        {
            ["Forwarder:Channels"] = channels
        });

        Assert.NotNull(provider.GetRequiredService<IDataDispatcher>());
    }

    [Fact]
    public void AddNitroCollection_ForwardChannels_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => BuildFullProvider(new Dictionary<string, string?>
        {
            ["Forwarder:Channels"] = "carrier-pigeon"
        }));
    }

    private static ServiceProvider BuildFullProvider(Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build());
        services.AddSingleton<IDeviceManager>(new FakeDeviceManager());
        services.AddSingleton<IDeviceHealthMonitor>(new FakeHealthMonitor());
        services.AddSingleton<IForwardBuffer>(new FakeForwardBuffer());
        services.AddSingleton<IMeasurementStore>(new NoopMeasurementStore());
        services.AddSingleton<IProtocolDriverPool>(new NoopDriverPool());
        return services.BuildServiceProvider();
    }

    private sealed class NoopMeasurementStore : IMeasurementStore
    {
        public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
            Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
            Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
            Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
        public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
            Guid deviceId, Guid? pointId, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
        public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class NoopDriverPool : IProtocolDriverPool
    {
        public IProtocolDriver GetOrCreate(Device device) => new NoopDriver();
        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    private sealed class NoopDriver : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }
}
