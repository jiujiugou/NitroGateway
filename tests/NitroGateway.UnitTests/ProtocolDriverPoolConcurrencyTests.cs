using System.Collections.Concurrent;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// <see cref="ProtocolDriverPool"/> 并发测试（《并发测试方法》P1/P2）。
///
/// <para>只覆盖两条<b>并发</b>主张，顺序语义（复用/重建/驱逐/全释放）见
/// <see cref="ProtocolDriverPoolTests"/>，不在此重复：</para>
/// <list type="bullet">
/// <item><b>线性化</b>：并发获取同一设备 → 只创建一个驱动（旧无锁实现可能投机创建多个）。</item>
/// <item><b>账平</b>：并发展与池销毁 → 不泄漏、不重复释放。</item>
/// </list>
/// </summary>
public class ProtocolDriverPoolConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 并发获取同一设备 → 恰好创建一个驱动，其余全部复用同一实例。
    /// <para><b>认证方式</b>：把 <c>ProtocolDriverPool.GetOrCreate</c> 的缓存命中判断改为"永不命中"，
    /// 本测试必须变红（创建数会变成线程数）。</para>
    /// </summary>
    [Fact]
    public async Task ConcurrentGetOrCreate_SameDevice_CreatesOnlyOneDriver()
    {
        var factory = new CountingFactory();
        using var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => pool.GetOrCreate(device))));

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, factory.CreatedCount);                    // 线性化：只创建一次
        Assert.All(results, d => Assert.Same(results[0], d));     // 全部复用同一实例
    }

    /// <summary>
    /// 获取与池销毁并发 → 所有创建过的驱动最终都被释放、且每个只释放一次。
    /// <para>用 <see cref="CountingFactory.BeforeCreate"/> 钩子把 worker 卡在创建处（此时它持有池内锁），
    /// 让 <c>Dispose</c> 去竞争同一把锁，从而稳定复现这个交错。</para>
    /// <para><b>认证方式</b>：删掉 <c>GetOrCreate</c> 里的 <c>_disposed</c> 双检，本测试应变红（销毁后又被加入 → 泄漏）。</para>
    /// </summary>
    [Fact]
    public async Task GetOrCreate_RacingPoolDispose_DoesNotLeak()
    {
        var factory = new CountingFactory();
        var pool = new ProtocolDriverPool(factory);
        var device = MakeDevice();

        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        factory.BeforeCreate = () => { entered.Release(); release.Wait(Timeout); };

        var worker = Task.Run(() =>
        {
            try { pool.GetOrCreate(device); }
            catch (ObjectDisposedException) { /* 销毁竞态下允许：worker 晚于销毁进入 */ }
        });

        await entered.WaitAsync(Timeout);       // worker 已进入临界区
        var dispose = Task.Run(pool.Dispose);   // 并发销毁：被池内锁挡住
        release.Release();                      // 放行 worker 完成创建

        await Task.WhenAll(worker, dispose).WaitAsync(Timeout);

        Assert.Equal(factory.CreatedCount, factory.DisposedCount);      // 账平
        Assert.All(factory.Drivers, d => Assert.Equal(1, d.DisposeCount));
    }

    /// <summary>构造测试设备。</summary>
    private static Device MakeDevice() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1:502" }
    };

    /// <summary>计数工厂：记录创建次数、持有全部驱动、提供创建前钩子。</summary>
    private sealed class CountingFactory : IProtocolDriverFactory
    {
        private readonly ConcurrentBag<CountingDriver> _drivers = new();
        private int _created;

        public Action? BeforeCreate { get; set; }
        public int CreatedCount => Volatile.Read(ref _created);
        public int DisposedCount => _drivers.Sum(d => d.DisposeCount);
        public IReadOnlyCollection<CountingDriver> Drivers => _drivers;

        public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
        {
            BeforeCreate?.Invoke();
            var driver = new CountingDriver();
            _drivers.Add(driver);
            Interlocked.Increment(ref _created);
            return driver;
        }
    }

    /// <summary>假驱动：用计数而非布尔追踪释放，以便发现"重复释放"。</summary>
    private sealed class CountingDriver : IProtocolDriver
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => throw new NotSupportedException("并发测试不使用单点读");
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(
            IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
