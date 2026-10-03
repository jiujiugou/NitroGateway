using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Protocol;

/// <summary>
/// 双接口释放（<see cref="IDisposable"/> + <see cref="IAsyncDisposable"/>）语义测试：
/// <list type="bullet">
/// <item>装饰器异步释放委托内层异步拆除，且与同步释放共用幂等位；</item>
/// <item>释放后到达的调用快速失败、不抛；</item>
/// <item>接口默认实现让「仅同步 Dispose」的替身也能被异步释放（零改 fake）；</item>
/// <item><see cref="ModbusDriverBase"/> 模板收口幂等，默认异步核心回退同步核心；</item>
/// <item>池 <c>EvictAsync</c> / <c>DisposeAsync</c> 走驱动异步释放。</item>
/// </list>
/// </summary>
public class ProtocolDriverDisposalTests
{
    // ───────────────── 装饰器 ─────────────────

    [Fact]
    public async Task Decorator_DisposeAsync_DelegatesToInnerAsync()
    {
        var inner = new AsyncTrackingDriver();
        var sut = Wrap(inner);

        await sut.DisposeAsync();

        Assert.Equal(1, inner.AsyncDisposeCount);
        Assert.Equal(0, inner.SyncDisposeCount);
    }

    [Fact]
    public async Task Decorator_SyncThenAsync_DisposesOnce()
    {
        var inner = new AsyncTrackingDriver();
        var sut = Wrap(inner);

        sut.Dispose();
        await sut.DisposeAsync();

        Assert.Equal(1, inner.SyncDisposeCount + inner.AsyncDisposeCount);
    }

    [Fact]
    public async Task Decorator_AsyncThenSync_DisposesOnce()
    {
        var inner = new AsyncTrackingDriver();
        var sut = Wrap(inner);

        await sut.DisposeAsync();
        sut.Dispose();

        Assert.Equal(1, inner.SyncDisposeCount + inner.AsyncDisposeCount);
    }

    [Fact]
    public async Task Decorator_AfterDisposeAsync_CallsFailFast()
    {
        var sut = Wrap(new AsyncTrackingDriver());
        await sut.DisposeAsync();

        Assert.True((await sut.ReadBatchAsync([])).IsFailure);
        Assert.True((await sut.ConnectAsync()).IsFailure);
        Assert.True((await sut.WriteAsync(MakePoint(), 1)).IsFailure);
        Assert.True((await sut.PingAsync()).IsFailure);
    }

    // ───────────────── 接口默认实现 ─────────────────

    [Fact]
    public async Task DefaultInterfaceDisposeAsync_FallsBackToSync()
    {
        var inner = new SyncOnlyDriver();

        await ((IAsyncDisposable)inner).DisposeAsync();

        Assert.Equal(1, inner.SyncDisposeCount);
    }

    // ───────────────── ModbusDriverBase 模板 ─────────────────

    [Fact]
    public async Task BaseDriver_Dispose_IsIdempotent_AndSharesFlagWithAsync()
    {
        var d = new CountingBaseDriver(NullLogger.Instance);

        d.Dispose();
        d.Dispose();
        await d.DisposeAsync();

        Assert.Equal(1, d.CoreDisposeCount);
        Assert.Equal(0, d.AsyncCoreDisposeCount);
    }

    [Fact]
    public async Task BaseDriver_AsyncThenSync_DisposesOnce()
    {
        var d = new CountingBaseDriver(NullLogger.Instance);

        await d.DisposeAsync();
        d.Dispose();

        Assert.Equal(1, d.CoreDisposeCount + d.AsyncCoreDisposeCount);
    }

    [Fact]
    public async Task BaseDriver_DefaultAsyncCore_FallsBackToSyncCore()
    {
        var d = new SyncCoreOnlyDriver(NullLogger.Instance);

        await d.DisposeAsync();

        Assert.Equal(1, d.CoreDisposeCount);
    }

    // ───────────────── 池 ─────────────────

    [Fact]
    public async Task Pool_EvictAsync_UsesDriverAsyncDisposal()
    {
        var factory = new AsyncCountingFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();
        pool.GetOrCreate(device);

        await pool.EvictAsync(device.Id);

        Assert.Equal(1, factory.AsyncDisposedCount);
        Assert.Equal(0, factory.SyncDisposedCount);
    }

    [Fact]
    public async Task Pool_DisposeAsync_UsesDriverAsyncDisposal()
    {
        var factory = new AsyncCountingFactory();
        var pool = new ProtocolDriverPool(factory);
        pool.GetOrCreate(MakeDevice());

        await pool.DisposeAsync();

        Assert.Equal(1, factory.AsyncDisposedCount);
        Assert.Equal(0, factory.SyncDisposedCount);
    }

    // ───────────────── 辅助 ─────────────────

    private static ReliableProtocolDriver Wrap(IProtocolDriver inner) => new(
        inner,
        NullLogger<ReliableProtocolDriver>.Instance,
        requestTimeout: TimeSpan.FromSeconds(1),
        maxRetryAttempts: 0,
        retryDelay: TimeSpan.FromMilliseconds(1));

    private static DevicePoint MakePoint() => new()
    {
        Name = "P",
        Address = "40001",
        DataType = DataType.Int16
    };

    private static Device MakeDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1:502" }
    };

    /// <summary>显式实现同步 + 异步释放的替身（计数分离，用于验证走的是哪条路）。</summary>
    private sealed class AsyncTrackingDriver : IProtocolDriver
    {
        private int _syncDispose;
        private int _asyncDispose;

        public int SyncDisposeCount => Volatile.Read(ref _syncDispose);
        public int AsyncDisposeCount => Volatile.Read(ref _asyncDispose);

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability { get; } = new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(
                new RawPointValue { Point = point, Value = (short)0, Timestamp = DateTime.UtcNow }));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public void Dispose() => Interlocked.Increment(ref _syncDispose);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _asyncDispose);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>只实现同步 Dispose 的替身：验证接口默认异步实现退化为同步。</summary>
    private sealed class SyncOnlyDriver : IProtocolDriver
    {
        private int _syncDispose;

        public int SyncDisposeCount => Volatile.Read(ref _syncDispose);

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability { get; } = new();

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
            => throw new NotSupportedException();
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() => Interlocked.Increment(ref _syncDispose);
    }

    /// <summary>实现 ModbusDriverBase 全部抽象成员的极简底座。</summary>
    private abstract class MinimalBaseDriver : ModbusDriverBase
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        protected MinimalBaseDriver(ILogger logger) : base(logger) { }

        protected override SemaphoreSlim Gate => _gate;
        protected override Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
            => Task.FromResult<object[]?>(null);
        protected override Task<object> ReadSingleTypedAsync(DataType type, string address)
            => Task.FromResult<object>((short)0);
        protected override Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
            => Task.FromResult(OperationResult.Success());
        public override Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    /// <summary>同步/异步核心分别计数，验证幂等与走的哪条路。</summary>
    private sealed class CountingBaseDriver : MinimalBaseDriver
    {
        private int _core;
        private int _asyncCore;

        public CountingBaseDriver(ILogger logger) : base(logger) { }

        public int CoreDisposeCount => Volatile.Read(ref _core);
        public int AsyncCoreDisposeCount => Volatile.Read(ref _asyncCore);

        protected override void DisposeCore() => Interlocked.Increment(ref _core);
        protected override ValueTask DisposeAsyncCore()
        {
            Interlocked.Increment(ref _asyncCore);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>只覆写同步核心：验证默认异步核心回退。</summary>
    private sealed class SyncCoreOnlyDriver : MinimalBaseDriver
    {
        private int _core;

        public SyncCoreOnlyDriver(ILogger logger) : base(logger) { }

        public int CoreDisposeCount => Volatile.Read(ref _core);

        protected override void DisposeCore() => Interlocked.Increment(ref _core);
    }

    /// <summary>计数工厂：用带异步释放的替身，分离同步/异步释放计数。</summary>
    private sealed class AsyncCountingFactory : IProtocolDriverFactory
    {
        private readonly List<AsyncTrackingDriver> _drivers = new();

        public int SyncDisposedCount => _drivers.Sum(d => d.SyncDisposeCount);
        public int AsyncDisposedCount => _drivers.Sum(d => d.AsyncDisposeCount);

        public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
        {
            var driver = new AsyncTrackingDriver();
            _drivers.Add(driver);
            return driver;
        }
    }
}
