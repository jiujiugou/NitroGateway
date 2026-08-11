using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Models;
using Xunit;
using AlarmRuleDomain = NitroGateway.Alarm.Domain.AlarmRule;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-022 控制器层测试：DeadLetters maxCount 夹紧、Devices 非法输入 400 / 忽略客户端 ID、
/// AlarmRules 非法 Guid/枚举 400。fakes 均记录调用供断言。
/// </summary>
public class WebapiControllerTests
{
    // ── DeadLettersController：P1-3 maxCount 夹紧 ──

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(99_999, 1000)]
    public async Task DeadLetters_GetAll_ClampsMaxCount(int input, int expected)
    {
        var buffer = new FakeForwardBuffer();
        var ctrl = new DeadLettersController(buffer);

        var result = await ctrl.GetAll(maxCount: input);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(expected, buffer.LastDeadLetterMax);
    }

    // ── DevicesController：P2-4 忽略客户端 ID / P2-1 非法枚举 400 / 空嵌套保护 ──

    [Fact]
    public async Task Devices_Create_IgnoresClientProvidedId()
    {
        var devices = new FakeDeviceManager();
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts());
        var clientId = Guid.NewGuid();
        var dto = new DeviceDto
        {
            Id = clientId.ToString(),
            Name = "dev",
            Protocol = new ProtocolDto { Name = "ModbusTcp" },
            Connection = new ConnectionDto { Endpoint = "127.0.0.1:502" },
            Status = "Online"
        };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(devices.LastRegistered);
        Assert.NotEqual(clientId, devices.LastRegistered!.Id);
    }

    [Fact]
    public async Task Devices_Create_NullProtocolAndConnection_DoesNotThrow()
    {
        var devices = new FakeDeviceManager();
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts());
        var dto = new DeviceDto { Id = "", Name = "dev", Protocol = null!, Connection = null!, Status = "Online" };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(devices.LastRegistered);
        Assert.Equal("", devices.LastRegistered!.Protocol.Name);
        Assert.Equal("", devices.LastRegistered!.Connection.Endpoint);
    }

    [Fact]
    public async Task Devices_UpdateStatus_InvalidEnum_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts());

        var result = await ctrl.UpdateStatus(Guid.NewGuid(), "BogusStatus");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_InvalidDataType_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts());
        var dto = new PointDto { DataType = "Bogus", Access = "ReadOnly" };

        var result = await ctrl.AddPoint(Guid.NewGuid(), dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_IgnoresClientProvidedId()
    {
        var points = new FakePointManager();
        var ctrl = new DevicesController(new FakeDeviceManager(), points, new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts());
        var clientPointId = Guid.NewGuid();
        var dto = new PointDto { Id = clientPointId.ToString(), Name = "p", Address = "1", DataType = "Float", Access = "ReadOnly" };

        var result = await ctrl.AddPoint(Guid.NewGuid(), dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(points.LastAdded);
        Assert.NotEqual(clientPointId, points.LastAdded!.Id);
    }

    // ────── DevicesController：连接测试语义（ADR-023）──────

    [Fact]
    public async Task Devices_TestConnection_ConnectAndPingOk_ReturnsSuccess()
    {
        var driver = new FakeProtocolDriver(OperationResult.Success(), OperationResult.Success());
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts());
        var dto = TestConnectionDto();

        var result = await ctrl.TestConnection(dto);
        var data = ReadTestData(result);

        Assert.True(data.success);
        Assert.Equal("ok", data.ping);
    }

    [Fact]
    public async Task Devices_TestConnection_ConnectOk_PingFail_ReturnsFailure()
    {
        var driver = new FakeProtocolDriver(OperationResult.Success(), OperationalError.Timeout("Ping 失败: 从站无响应"));
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts());
        var dto = TestConnectionDto();

        var result = await ctrl.TestConnection(dto);
        var data = ReadTestData(result);

        Assert.False(data.success);
        Assert.Contains("从站无响应", data.error);
    }

    [Fact]
    public async Task Devices_TestConnection_ConnectFail_ReturnsFailure()
    {
        var driver = new FakeProtocolDriver(OperationalError.Communication("Modbus 连接失败: 拒绝连接"), OperationResult.Success());
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts());
        var dto = TestConnectionDto();

        var result = await ctrl.TestConnection(dto);
        var data = ReadTestData(result);

        Assert.False(data.success);
        Assert.Contains("拒绝连接", data.error);
    }

    private static TestConnectionData ReadTestData(ActionResult<ApiResponse<object>> result)
    {
        var data = ((ApiResponse<object>)((OkObjectResult)result.Result!).Value!).Data!;
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new TestConnectionData(
            root.GetProperty("success").GetBoolean(),
            root.TryGetProperty("error", out var err) ? err.GetString() : null,
            root.TryGetProperty("ping", out var ping) ? ping.GetString() : null);
    }

    private sealed record TestConnectionData(bool success, string? error, string? ping);

    private static DeviceDto TestConnectionDto() => new()
    {
        Name = "test",
        Protocol = new ProtocolDto { Name = "Modbus", Dialect = "TCP" },
        Connection = new ConnectionDto { Endpoint = "127.0.0.1:502", Parameters = new Dictionary<string, object> { ["UnitId"] = 11 } }
    };
    // ── AlarmRulesController：P2-1 非法 Guid/枚举 400 ──

    [Fact]
    public async Task AlarmRules_Create_InvalidSeverity_ReturnsBadRequest()
    {
        var ctrl = new AlarmRulesController(new FakeAlarmRuleRepository());
        var dto = ValidRuleDto();
        dto.Severity = "Bogus";

        var result = await ctrl.Create(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AlarmRules_Create_InvalidDeviceId_ReturnsBadRequest()
    {
        var ctrl = new AlarmRulesController(new FakeAlarmRuleRepository());
        var dto = ValidRuleDto();
        dto.DeviceId = "not-a-guid";

        var result = await ctrl.Create(dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AlarmRules_Update_InvalidPointId_ReturnsBadRequest()
    {
        var ctrl = new AlarmRulesController(new FakeAlarmRuleRepository());
        var dto = ValidRuleDto();
        dto.PointId = "not-a-guid";

        var result = await ctrl.Update(Guid.NewGuid(), dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AlarmRules_Create_Valid_SavesRule()
    {
        var repo = new FakeAlarmRuleRepository();
        var ctrl = new AlarmRulesController(repo);

        var result = await ctrl.Create(ValidRuleDto());

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(repo.LastSaved);
    }

    private static AlarmRuleDto ValidRuleDto() => new()
    {
        Id = Guid.NewGuid().ToString(),
        DeviceId = Guid.NewGuid().ToString(),
        PointId = Guid.NewGuid().ToString(),
        Operator = ">",
        Threshold = 70,
        Severity = "Warning",
        Enabled = true
    };
}

// ═══════════ fakes ═══════════

public sealed class FakeDeviceManager : IDeviceManager
{
    public Device? LastRegistered { get; private set; }

    public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
    {
        LastRegistered = device;
        return Task.FromResult(OperationResult<Device>.Success(device));
    }

    public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Failure(OperationalError.NotFound("设备不存在")));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());

    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());

    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}

public sealed class FakePointManager : IPointManager
{
    public DevicePoint? LastAdded { get; private set; }

    public Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
    {
        LastAdded = point;
        return Task.FromResult(OperationResult<DevicePoint>.Success(point));
    }

    public Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> UpdateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<DevicePoint>>.Success(points));

    public Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<DevicePoint>>>(Array.Empty<DevicePoint>());

    public Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<PointValidationError>>>(Array.Empty<PointValidationError>());
}

public sealed class FakeHealthMonitor : IDeviceHealthMonitor
{
    public void ReportSuccess(Guid deviceId, string? deviceName) { }
    public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
    public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
    public int FailureThreshold => 3;
    public int RecoveryThreshold => 3;
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
    public void Remove(Guid deviceId) { }
    public void AddListener(IDeviceHealthListener listener) { }
}

public sealed class FakeDriverFactory : IProtocolDriverFactory
{
    public FakeDriverFactory(IProtocolDriver? driver = null) => Driver = driver;

    public IProtocolDriver? Driver { get; }

    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
        => Driver ?? throw new NotImplementedException("连接测试用例需要注入 FakeProtocolDriver");
}

public sealed class FakeSerialPorts : ISerialPortManager
{
    public SerialPortLease Acquire(SerialPortSettings settings)
        => throw new NotImplementedException("串口用例不需要真实租约");
    public IReadOnlyList<string> GetAvailablePorts() => [];
    public IReadOnlyList<SerialPortInfo> GetStatus() => [];
}

public sealed class FakeForwardBuffer : IForwardBuffer
{
    public int? LastDeadLetterMax { get; private set; }

    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<BatchMeasurements>>>(Array.Empty<BatchMeasurements>());

    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
    {
        LastDeadLetterMax = maxCount;
        return Task.FromResult<OperationResult<IReadOnlyList<DeadLetterEntry>>>(Array.Empty<DeadLetterEntry>());
    }

    public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public int Count => 0;

    public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0);
}

public sealed class FakeAlarmRuleRepository : IAlarmRuleRepository
{
    public AlarmRuleDomain? LastSaved { get; private set; }

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetByPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<AlarmRuleDomain>>>(Array.Empty<AlarmRuleDomain>());

    public Task<OperationResult> SaveAsync(AlarmRuleDomain rule, CancellationToken ct = default)
    {
        LastSaved = rule;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}
public sealed class FakeProtocolDriver : IProtocolDriver
{
    private readonly OperationResult _connectResult;
    private readonly OperationResult _pingResult;

    public FakeProtocolDriver(OperationResult connectResult, OperationResult pingResult)
    {
        _connectResult = connectResult;
        _pingResult = pingResult;
    }

    public DriverState State => DriverState.Connected;
    public DriverCapability Capability => new();

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult(_connectResult);

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> PingAsync(CancellationToken ct = default)
        => Task.FromResult(_pingResult);

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

    public void Dispose() { }
}
