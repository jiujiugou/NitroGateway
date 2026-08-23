using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Security.Guard;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// IWriteService（WriteService）写链路测试（docs/14 §3.2）：
/// 设备/点位解析 → Access/Enabled 校验 → 值类型转换 → WriteGuard 三级门控 →
/// 反向缩放（工程值→原始值）→ 驱动池取长连接 → WriteAsync。
/// 覆盖：成功写、只读/禁用拒绝、超范围/变化率/离线拒绝、缩放开算、
/// Bool/String 写、值转换失败、设备/点位不存在、驱动写失败。
/// </summary>
public sealed class WriteServiceTests
{
    private readonly WriteGuard _guard = new(
        new RangeValidator(), new RateLimitValidator(), new ModeValidator(),
        NullLogger<WriteGuard>.Instance);
    private readonly ModeValidator _mode = new();

    // ═══════════════════ 成功路径 ═══════════════════

    /// <summary>设备 Online + 值在范围 + 驱动已连接 → 写成功，驱动收到工程值（恒等缩放）。</summary>
    [Fact]
    public async Task WriteAsync_Success_WritesValueToDriver()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(point.Id, svc.Driver.WrittenPoint?.Id);
        Assert.Equal(50f, Assert.IsType<float>(svc.Driver.WrittenValue)); // ScaleFactor=1/Offset=0 恒等
    }

    /// <summary>工程值反向缩放：ScaleFactor=2、ScaleOffset=10，工程值 50 → 原始值 (50−10)/2=20。</summary>
    [Fact]
    public async Task WriteAsync_ScaledPoint_ReverseScalesBeforeWrite()
    {
        var point = WritablePoint(min: 0, max: 1000);
        point.ScaleFactor = 2;
        point.ScaleOffset = 10;
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        // 反算走 double 运算，驱动收到 double 20.0
        Assert.Equal(20.0, Assert.IsType<double>(svc.Driver.WrittenValue));
    }

    /// <summary>Bool 点位：写 true → 驱动收到 bool true（按 0/1 过门控）。</summary>
    [Fact]
    public async Task WriteAsync_BoolPoint_WritesBool()
    {
        var point = WritablePoint(DataType.Bool, min: 0, max: 1);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, true));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(true, svc.Driver.WrittenValue);
    }

    /// <summary>String 点位：只做在线校验，字符串原样下发驱动。</summary>
    [Fact]
    public async Task WriteAsync_StringPoint_WritesString()
    {
        var point = WritablePoint(DataType.String);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, "abc"));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("abc", svc.Driver.WrittenValue);
    }

    // ═══════════════════ Access / Enabled 拒绝 ═══════════════════

    /// <summary>只读点位不可写 → 校验失败，驱动不被调用。</summary>
    [Fact]
    public async Task WriteAsync_ReadOnlyPoint_Rejected()
    {
        var point = WritablePoint();
        point.Access = PointAccess.ReadOnly;
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsFailure);
        Assert.Contains("只读", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    /// <summary>已禁用点位不可写 → 校验失败，驱动不被调用。</summary>
    [Fact]
    public async Task WriteAsync_DisabledPoint_Rejected()
    {
        var point = WritablePoint();
        point.Enabled = false;
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsFailure);
        Assert.Contains("禁用", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    // ═══════════════════ WriteGuard 门控拒绝 ═══════════════════

    /// <summary>值超上限（150 > 100）→ 拒绝，驱动不被调用。</summary>
    [Fact]
    public async Task WriteAsync_OutOfRange_Rejected()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 150));

        Assert.True(result.IsFailure);
        Assert.Contains("范围", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    /// <summary>设备离线（健康快照 Offline）→ Mode 门控拒绝。</summary>
    [Fact]
    public async Task WriteAsync_DeviceOffline_Rejected()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Offline, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsFailure);
        Assert.Contains("不在线", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    /// <summary>变化率超限：上次 100 → 新值 300（+200% > 100%）→ 拒绝。</summary>
    [Fact]
    public async Task WriteAsync_RateLimitExceeded_Rejected()
    {
        var point = WritablePoint(min: 0, max: 1000);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var store = new StagedMeasurementStore();
        store.EnqueueLatest(Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(
            [TestDevices.Snapshot(device.Id, point.Id, 100)])));
        var svc = MakeService(device, health: Online, driver: new FakeDriver(), store: store);

        var result = await svc.Service.WriteAsync(Req(device, point, 300));

        Assert.True(result.IsFailure);
        Assert.Contains("变化率", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    // ═══════════════════ 解析失败 ═══════════════════

    /// <summary>设备不存在 → NotFound。</summary>
    [Fact]
    public async Task WriteAsync_DeviceNotFound_Fails()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50) with { DeviceId = Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Contains("设备不存在", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    /// <summary>点位不存在 → NotFound。</summary>
    [Fact]
    public async Task WriteAsync_PointNotFound_Fails()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, 50) with { PointId = Guid.NewGuid() });

        Assert.True(result.IsFailure);
        Assert.Contains("点位不存在", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    /// <summary>值无法转换为点位类型（Int32 点写 "abc"）→ 校验失败。</summary>
    [Fact]
    public async Task WriteAsync_ValueTypeMismatch_Rejected()
    {
        var point = WritablePoint(DataType.Int32, min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var svc = MakeService(device, health: Online, driver: new FakeDriver());

        var result = await svc.Service.WriteAsync(Req(device, point, "abc"));

        Assert.True(result.IsFailure);
        Assert.Contains("无法转换", result.Error!.Message);
        Assert.Equal(0, svc.Driver.WriteCallCount);
    }

    // ═══════════════════ 驱动层 ═══════════════════

    /// <summary>驱动未连接时先建连，成功后再写。</summary>
    [Fact]
    public async Task WriteAsync_DriverDisconnected_ConnectsThenWrites()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var driver = new FakeDriver { State = DriverState.Disconnected };
        var svc = MakeService(device, health: Online, driver: driver);

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(driver.ConnectCalled);
        Assert.Equal(1, driver.WriteCallCount);
    }

    /// <summary>驱动写失败 → 结果失败并携带原因。</summary>
    [Fact]
    public async Task WriteAsync_DriverFailure_ReturnsFailure()
    {
        var point = WritablePoint(min: 0, max: 100);
        var device = TestDevices.Device("设备A", point);
        device.Status = DeviceStatus.Online;
        var driver = new FakeDriver { WriteResult = OperationResult.Failure(OperationalError.Communication("从站无响应")) };
        var svc = MakeService(device, health: Online, driver: driver);

        var result = await svc.Service.WriteAsync(Req(device, point, 50));

        Assert.True(result.IsFailure);
        Assert.Contains("从站无响应", result.Error!.Message);
        Assert.Equal(1, driver.WriteCallCount);
    }

    // ── Helpers ──

    private static WriteRequest Req(Device device, DevicePoint point, object value) => new()
    {
        DeviceId = device.Id,
        PointId = point.Id,
        Value = value
    };

    private static DevicePoint WritablePoint(DataType type = DataType.Float, double? min = null, double? max = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "P1",
        Address = "40001",
        DataType = type,
        Enabled = true,
        Access = PointAccess.ReadWrite,
        MinLimit = min,
        MaxLimit = max
    };

    private readonly record struct HealthStatus(DeviceStatus Status);

    private static HealthStatus Online => new(DeviceStatus.Online);
    private static HealthStatus Offline => new(DeviceStatus.Offline);

    private Harness MakeService(
        Device device, HealthStatus health, FakeDriver driver, StagedMeasurementStore? store = null)
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(device);
        return new Harness(
            new WriteService(
                cache,
                new FakeHealthMonitor(health.Status),
                store ?? new StagedMeasurementStore(),
                new FakeDriverPool(driver),
                _guard,
                _mode,
                NullLogger<WriteService>.Instance),
            driver);
    }

    private sealed record Harness(IWriteService Service, FakeDriver Driver);

    private sealed class FakeHealthMonitor(DeviceStatus status) : IDeviceHealthMonitor
    {
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) =>
            new() { DeviceId = deviceId, Status = status };
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
        public void Remove(Guid deviceId) { }
        public void AddListener(IDeviceHealthListener listener) { }
    }

    private sealed class FakeDriverPool(IProtocolDriver driver) : IProtocolDriverPool
    {
        public IProtocolDriver GetOrCreate(Device device) => driver;
        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    private sealed class FakeDriver : IProtocolDriver
    {
        public DriverState State { get; set; } = DriverState.Connected;
        public OperationResult WriteResult { get; set; } = OperationResult.Success();
        public bool ConnectCalled { get; private set; }
        public DevicePoint? WrittenPoint { get; private set; }
        public object? WrittenValue { get; private set; }
        public int WriteCallCount { get; private set; }

        public DriverCapability Capability => new();
        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            ConnectCalled = true;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 1f, Timestamp = DateTime.UtcNow }));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success([]));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        {
            WrittenPoint = point;
            WrittenValue = value;
            WriteCallCount++;
            return Task.FromResult(WriteResult);
        }
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }
}
