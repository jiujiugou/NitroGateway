using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 长连接驱动池单元测试——验证"按设备复用、参数变化重建、Evict 驱逐"三条语义。
/// 用 FakeDriverFactory 计数驱动创建次数，用 FakeDriver 跟踪 Dispose。
/// </summary>
public class ProtocolDriverPoolTests
{
    [Fact]
    public void GetOrCreate_SameParams_ReturnsSameInstance()
    {
        var factory = new FakeDriverFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        var first = pool.GetOrCreate(device);
        var second = pool.GetOrCreate(device);

        Assert.Same(first, second);
        Assert.Equal(1, factory.CreatedCount);
        Assert.False(factory.Drivers[0].Disposed);
    }

    [Fact]
    public void GetOrCreate_ChangedEndpoint_RebuildsAndDisposesOld()
    {
        var factory = new FakeDriverFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        var first = pool.GetOrCreate(device);
        device.Connection.Endpoint = "10.0.0.2:502";
        var second = pool.GetOrCreate(device);

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.CreatedCount);
        Assert.True(factory.Drivers[0].Disposed);
        Assert.False(factory.Drivers[1].Disposed);
    }

    [Fact]
    public void GetOrCreate_ChangedParameters_Rebuilds()
    {
        var factory = new FakeDriverFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        pool.GetOrCreate(device);
        device.Connection.Parameters["UnitId"] = 2;
        pool.GetOrCreate(device);

        Assert.Equal(2, factory.CreatedCount);
        Assert.True(factory.Drivers[0].Disposed);
    }

    [Fact]
    public void Evict_DisposesAndNextCallRebuilds()
    {
        var factory = new FakeDriverFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        var first = pool.GetOrCreate(device);
        pool.Evict(device.Id);
        var second = pool.GetOrCreate(device);

        Assert.NotSame(first, second);
        Assert.True(factory.Drivers[0].Disposed);
        Assert.Equal(2, factory.CreatedCount);
    }

    [Fact]
    public void Dispose_DisposesAllCachedDrivers()
    {
        var factory = new FakeDriverFactory();
        var pool = new ProtocolDriverPool(factory);
        pool.GetOrCreate(MakeDevice());
        pool.GetOrCreate(MakeDevice());

        pool.Dispose();

        Assert.All(factory.Drivers, d => Assert.True(d.Disposed));
    }

    private static Device MakeDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection
        {
            Endpoint = "192.168.1.1:502",
            ConnectTimeoutMs = 3000,
            RequestTimeoutMs = 5000,
            RetryCount = 3,
            RetryIntervalMs = 1000,
            Parameters = new Dictionary<string, object> { ["UnitId"] = 1 }
        }
    };

    /// <summary>记录创建次数与实例列表的假工厂</summary>
    private sealed class FakeDriverFactory : IProtocolDriverFactory
    {
        public List<FakeDriver> Drivers { get; } = new();
        public int CreatedCount => Drivers.Count;

        public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
        {
            var driver = new FakeDriver();
            Drivers.Add(driver);
            return driver;
        }
    }

    private sealed class FakeDriver : IProtocolDriver
    {
        public bool Disposed { get; private set; }
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));

        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public void Dispose() => Disposed = true;
    }
}
