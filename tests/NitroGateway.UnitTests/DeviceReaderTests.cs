using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-030 L2（用户决策）：空点位设备不跳过连接——仍从连接池取驱动并尝试连接，
/// 连接失败经重试机制上报失败 → 连续失败判离线；连接成功返回空列表。
/// </summary>
public class DeviceReaderTests
{
    [Fact]
    public async Task ReadDeviceAsync_NoEnabledPoints_StillUsesPool_ReturnsEmptySuccess()
    {
        var driver = new FakeDriver { ReadResult = OperationResult<IReadOnlyList<RawPointValue>>.Success([]) };
        var pool = new FakeDriverPool(driver);
        var reader = new DeviceReader(pool, NullLogger<DeviceReader>.Instance);

        var result = await reader.ReadDeviceAsync(MakeDevice([]), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value!);
        Assert.True(pool.GetOrCreateCalled, "空点位设备也必须走连接池（建连/复用长连接）");
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
        var reader = new DeviceReader(pool, NullLogger<DeviceReader>.Instance);

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
        var reader = new DeviceReader(pool, NullLogger<DeviceReader>.Instance);

        var result = await reader.ReadDeviceAsync(
            MakeDevice([new DevicePoint { Id = raw.Point.Id, Name = "P1", Address = "0", Enabled = true }]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
    }

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

        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 1f, Timestamp = DateTime.UtcNow }));

        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(ReadResult);

        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }
}
