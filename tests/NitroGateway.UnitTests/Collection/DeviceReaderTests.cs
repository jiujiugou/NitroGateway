using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Collection;

public class DeviceReaderTests
{
    [Fact]
    public async Task ReadDeviceAsync_NoEnabledPoints_StillUsesPool_ReturnsEmptySuccess()
    {
        var driver = new FakeDriver { ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Success([]) };
        var pool = new FakeDriverPool(driver);
        var reader = MakeReader(pool, driver);

        var result = await reader.ReadDeviceAsync(MakeDevice([]), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value!);
        Assert.True(pool.GetOrCreateCalled, "空点位设备也必须走连接池（建连/复用长连接）");
        Assert.Null(reader.GetDuePoints(MakeDevice([]))); // 无 enabled 点位 → GetDuePoints 返回 null（仍探活）
    }

    [Fact]
    public async Task ReadDeviceAsync_NoEnabledPoints_ConnectFailure_ReturnsFailure()
    {
        var driver = new FakeDriver
        {
            ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Failure(
                OperationalError.Communication("从站无响应"))
        };
        var pool = new FakeDriverPool(driver);
        var reader = MakeReader(pool, driver);

        var result = await reader.ReadDeviceAsync(MakeDevice([]), CancellationToken.None);

        Assert.True(result.IsFailure, "连接/读取失败必须上报失败，供健康监控判离线");
        Assert.True(pool.GetOrCreateCalled);
    }

    [Fact]
    public async Task ReadDeviceAsync_EnabledPoints_ReadsValues()
    {
        var raw = new RawPointValue
        {
            Point = new DevicePoint { Id = Guid.NewGuid(), Name = "P1", Address = "0", DataType = DataType.Float },
            Value = 12.5f,
            Timestamp = DateTime.UtcNow
        };
        var driver = new FakeDriver { ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Success([raw]) };
        var pool = new FakeDriverPool(driver);
        var reader = MakeReader(pool, driver);

        var result = await reader.ReadDeviceAsync(
            MakeDevice([new DevicePoint { Id = raw.Point.Id, Name = "P1", Address = "0", Enabled = true }]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }


    /// <summary>ScanIntervalMs=0（默认）→ 继承全局 1000ms → 每轮（按全局间隔）都读。</summary>
    [Fact]
    public async Task ReadDeviceAsync_ScanIntervalZero_ReadsEveryRound()
    {
        var clock = new FakeClock();
        var driver = new FakeDriver { ReadResult = SuccessResult() };
        var reader = MakeReader(new FakeDriverPool(driver), driver, clock: () => clock.Now);
        var point = MakePoint(scanIntervalMs: 0);
        var device = MakeDevice([point]);

        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=0 首读
        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=1000 到期 → 读
        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=2000 到期 → 读

        Assert.Equal(3, driver.BatchCallCount);
    }

    /// <summary>ScanIntervalMs=3000 + 全局 1000 → 第 0/3/6 轮读，中间轮跳过（不调驱动）。</summary>
    [Fact]
    public async Task ReadDeviceAsync_ScanInterval3000_SamplesEveryThreeRounds()
    {
        var clock = new FakeClock();
        var driver = new FakeDriver { ReadResult = SuccessResult() };
        var reader = MakeReader(new FakeDriverPool(driver), driver, clock: () => clock.Now);
        var point = MakePoint(scanIntervalMs: 3000);
        var device = MakeDevice([point]);

        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=0 首读
        Assert.Equal(1, driver.BatchCallCount);

        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=1000 未到期 → 跳过
        Assert.Equal(1, driver.BatchCallCount);

        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=2000 未到期 → 跳过
        Assert.Equal(1, driver.BatchCallCount);

        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=3000 到期 → 读
        Assert.Equal(2, driver.BatchCallCount);

        clock.Advance(1000);
        await reader.ReadDeviceAsync(device, CancellationToken.None); // t=4000 未到期 → 跳过
        Assert.Equal(2, driver.BatchCallCount);
    }

    /// <summary>首次/新点位无历史缓存 → 立即读。</summary>
    [Fact]
    public void GetDuePoints_FirstCall_ReturnsPointImmediately()
    {
        var reader = MakeReader(new FakeDriverPool(new FakeDriver()), new FakeDriver());
        var point = MakePoint(scanIntervalMs: 5000);
        var device = MakeDevice([point]);

        var due = reader.GetDuePoints(device);

        Assert.NotNull(due);
        var duePoint = Assert.Single(due!);
        Assert.Equal(point.Id, duePoint.Id);
    }

    /// <summary>全部未到期 → GetDuePoints 返回空列表（跳过标记）。</summary>
    [Fact]
    public void GetDuePoints_AllNotDue_ReturnsEmpty()
    {
        var clock = new FakeClock();
        var driver = new FakeDriver { ReadResult = SuccessResult() };
        var reader = MakeReader(new FakeDriverPool(driver), driver, clock: () => clock.Now);
        var point = MakePoint(scanIntervalMs: 3000);
        var device = MakeDevice([point]);

        // 首读立即到期
        Assert.NotEmpty(reader.GetDuePoints(device)!);
        // 模拟一轮扫描后 1000ms：未到 3000ms 间隔 → 空
        reader.ReadDeviceAsync(device, CancellationToken.None).GetAwaiter().GetResult();
        clock.Advance(1000);

        Assert.Empty(reader.GetDuePoints(device)!);
    }

    /// <summary>多设备并发下 ConcurrentDictionary 线程安全：各自独立调度不串扰。</summary>
    [Fact]
    public async Task GetDuePoints_MultipleDevicesConcurrent_IsThreadSafe()
    {
        var clock = new FakeClock();
        var driver = new FakeDriver { ReadResult = SuccessResult() };
        var reader = MakeReader(new FakeDriverPool(driver), driver, clock: () => clock.Now);
        var devices = Enumerable.Range(0, 20)
            .Select(i => MakeDevice([MakePoint(scanIntervalMs: 2000, suffix: $"P{i}")]))
            .ToList();

        // 并发首轮：全部应立即到期（无历史）
        await Task.WhenAll(devices.Select(d => reader.ReadDeviceAsync(d, CancellationToken.None)));
        Assert.Equal(20, driver.BatchCallCount);

        // 并发次轮（t=1000，未到期）：全部应跳过，不再调驱动
        clock.Advance(1000);
        await Task.WhenAll(devices.Select(d => reader.ReadDeviceAsync(d, CancellationToken.None)));
        Assert.Equal(20, driver.BatchCallCount);
    }

    /// <summary>ScanIntervalMs=0 继承全局 1000ms（不是 0/立即）：500ms 时不应到期。</summary>
    [Fact]
    public void GetDuePoints_ZeroScanInterval_InheritsGlobalInterval()
    {
        var clock = new FakeClock();
        var driver = new FakeDriver { ReadResult = SuccessResult() };
        var reader = MakeReader(new FakeDriverPool(driver), driver, clock: () => clock.Now);
        var point = MakePoint(scanIntervalMs: 0);
        var device = MakeDevice([point]);

        Assert.NotEmpty(reader.GetDuePoints(device)!); // t=0 首读立即到期
        reader.ReadDeviceAsync(device, CancellationToken.None).GetAwaiter().GetResult(); // 标记 t=0 已扫描
        clock.Advance(500);

        Assert.Empty(reader.GetDuePoints(device)!); // 500 < 全局 1000 → 未到期（若误用 0 间隔则会到期）
    }

    /// <summary>驱动抛出取消异常必须原样向上传播（不被归一为 Protocol 失败）。</summary>
    [Fact]
    public async Task ReadDeviceAsync_DriverCancellation_Propagates()
    {
        var driver = new FakeDriver { ReadException = new OperationCanceledException("stop") };
        var reader = MakeReader(new FakeDriverPool(driver), driver);
        var device = MakeDevice([MakePoint()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadDeviceAsync(device, CancellationToken.None));
    }

    /// <summary>驱动通信抛异常 → Reader 必须归一为 OperationResult 失败，不得向上抛。</summary>
    [Fact]
    public async Task ReadDeviceAsync_DriverThrows_ReturnsOperationalError()
    {
        var driver = new FakeDriver { ReadException = new InvalidOperationException("socket boom") };
        var reader = MakeReader(new FakeDriverPool(driver), driver);
        var device = MakeDevice([MakePoint()]);

        var result = await reader.ReadDeviceAsync(device, CancellationToken.None);

        Assert.True(result.IsFailure, "驱动异常必须归一为失败结果");
        Assert.Contains("socket boom", result.Error!.Message);
    }

    // ── Helpers ──

    private static OperationResult<IReadOnlyList<RawPointValue>> SuccessResult() =>
        OperationResult<IReadOnlyList<RawPointValue>>.Success([]);

    private static DeviceReader MakeReader(
        FakeDriverPool pool, FakeDriver driver, Func<DateTime>? clock = null)
        => new(pool, Options.Create(new CollectionOption { IntervalMs = 1000 }),
            NullLogger<DeviceReader>.Instance, clock);

    private static DevicePoint MakePoint(int scanIntervalMs = 0, string? suffix = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"P{suffix ?? "1"}",
        Address = "40001",
        DataType = DataType.Float,
        Enabled = true,
        ScanIntervalMs = scanIntervalMs
    };

    private static Device MakeDevice(IReadOnlyCollection<DevicePoint> points)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = "空点位设备",
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
            Connection = new DeviceConnection
            {
                Endpoint = "127.0.0.1:502",
                ConnectTimeoutMs = 3000,
                RequestTimeoutMs = 5000,
                RetryCount = 3,
                RetryIntervalMs = 1000,
                Parameters = new Dictionary<string, object> { ["UnitId"] = 1 }
            }
        };
        foreach (var p in points)
            device.AddPoint(p);
        return device;
    }

    private sealed class FakeClock
    {
        public DateTime Now { get; private set; } = DateTime.UtcNow;
        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
    }

    private sealed class FakeDriverPool : IProtocolDriverPool
    {
        private readonly IProtocolDriver _driver;
        public FakeDriverPool(IProtocolDriver driver) => _driver = driver;
        public bool GetOrCreateCalled { get; private set; }

        public IProtocolDriver GetOrCreate(Device device)
        {
            GetOrCreateCalled = true;
            return _driver;
        }

        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    private sealed class FakeDriver : IProtocolDriver
    {
        public OperationResult<IReadOnlyList<RawPointValue>> ReadResult { get; init; } =
            OperationResult<IReadOnlyList<RawPointValue>>.Success([]);
        /// <summary>非 null 时 ReadBatchAsync 抛出该异常（验证 Reader 的异常归一化）。</summary>
        public Exception? ReadException { get; init; }
        public int BatchCallCount { get; private set; }

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 1f, Timestamp = DateTime.UtcNow }));

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            BatchCallCount++;
            if (ReadException is not null)
                throw ReadException;
            return Task.FromResult(ReadResult);
        }

        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }
}
