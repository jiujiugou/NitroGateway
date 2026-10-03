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
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Storage.Buffer;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;
using Xunit;
using AlarmRuleDomain = NitroGateway.Alarm.Domain.AlarmRule;

namespace NitroGateway.UnitTests.Webapi;

public class WebapiControllerTests
{

    // ── DevicesController：P2-4 忽略客户端 ID / P2-1 非法枚举 400 / 空嵌套保护 ──

    [Fact]
    public async Task Devices_Create_IgnoresClientProvidedId()
    {
        var devices = new FakeDeviceManager();
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
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
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
        var dto = new DeviceDto { Id = "", Name = "dev", Protocol = null!, Connection = null!, Status = "Online" };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(devices.LastRegistered);
        Assert.Equal("", devices.LastRegistered!.Protocol.Name);
        Assert.Equal("", devices.LastRegistered!.Connection.Endpoint);
    }

    [Fact]
    public async Task Devices_Export_ReturnsSnapshotWithDevicesAndPoints()
    {
        // ADR-033 阶段 2：导出端点返回 devices+points 全量，供现场「从中心导入」
        var devices = new FakeDeviceManager();
        var device = TestDevices.Device("1号车间 PLC");
        device.AddPoint(TestDevices.Point("炉温"));
        devices.AllDevices = new[] { device };
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);

        var result = await ctrl.Export();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<DeviceDto>>>(ok.Value);
        Assert.True(body.Success);
        var exported = Assert.Single(body.Data!);
        Assert.Equal(device.Id.ToString(), exported.Id);
        Assert.Equal(device.Name, exported.Name);
        Assert.Equal("Modbus", exported.Protocol.Name);
        Assert.Equal(device.Connection.Endpoint, exported.Connection.Endpoint);
        Assert.Single(exported.Points);
    }
    [Fact]
    public async Task Devices_Create_PreservesSiteId()
    {
        // ADR-035 方案 A：Web 建设备可指定站点归属，落库保留
        var devices = new FakeDeviceManager();
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
        var dto = new DeviceDto
        {
            Name = "dev",
            Protocol = new ProtocolDto { Name = "ModbusTcp" },
            Connection = new ConnectionDto { Endpoint = "127.0.0.1:502" },
            Status = "Online",
            SiteId = "site-a"
        };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("site-a", devices.LastRegistered!.SiteId);
    }

    [Fact]
    public async Task Devices_Create_MissingSiteId_DefaultsToProviderCurrent()
    {
        // ADR-054：web 作为纯边缘单一身份——前端不传 siteId，设备归属=本站点（SiteIdProvider.Current）
        var devices = new FakeDeviceManager();
        var ctrl = new DevicesController(
            devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(),
            new FakeSiteIdProvider { Current = "edge-site-1" }, new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
        var dto = new DeviceDto
        {
            Name = "dev",
            Protocol = new ProtocolDto { Name = "ModbusTcp" },
            Connection = new ConnectionDto { Endpoint = "127.0.0.1:502" },
            Status = "Online"
        };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("edge-site-1", devices.LastRegistered!.SiteId);
    }

    [Fact]
    public async Task Devices_Create_WritesOutboxOnSuccess()
    {
        // ADR-033 阶段 4：设备落库成功后写 outbox，同步服务联网后上报中心
        var outbox = new FakeConfigSyncOutboxStore();
        var ctrl = new DevicesController(
            new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(),
            new FakeSiteIdProvider(), outbox, NullLogger<DevicesController>.Instance);
        var dto = new DeviceDto
        {
            Name = "dev",
            Protocol = new ProtocolDto { Name = "ModbusTcp" },
            Connection = new ConnectionDto { Endpoint = "127.0.0.1:502" },
            Status = "Online"
        };

        var result = await ctrl.Create(dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, outbox.RecordDeviceCalls);
    }

    [Fact]
    public async Task Devices_UpdateStatus_InvalidEnum_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);

        var result = await ctrl.UpdateStatus(Guid.NewGuid(), "BogusStatus");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_InvalidDataType_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
        var dto = new PointDto { DataType = "Bogus", Access = "ReadOnly" };

        var result = await ctrl.AddPoint(Guid.NewGuid(), dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_IgnoresClientProvidedId()
    {
        var points = new FakePointManager();
        var ctrl = new DevicesController(new FakeDeviceManager(), points, new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
        var clientPointId = Guid.NewGuid();
        var dto = new PointDto { Id = clientPointId.ToString(), Name = "p", Address = "1", DataType = "Float", Access = "ReadOnly" };

        var result = await ctrl.AddPoint(Guid.NewGuid(), dto);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(points.LastAdded);
        Assert.NotEqual(clientPointId, points.LastAdded!.Id);
    }


    [Fact]
    public async Task Devices_TestConnection_ConnectAndPingOk_ReturnsSuccess()
    {
        var driver = new FakeProtocolDriver(OperationResult.Success(), OperationResult.Success());
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
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
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
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
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), new FakeSiteIdProvider(), new FakeConfigSyncOutboxStore(), NullLogger<DevicesController>.Instance);
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


    [Fact]
    public void Site_Get_ReturnsCurrentIdentity()
    {
        var provider = new FakeSiteIdProvider { Current = "edge-plant-1", Source = SiteIdSource.Persisted };
        var ctrl = new SiteController(provider, NullLogger<SiteController>.Instance);

        var result = ctrl.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<SiteIdentityDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("edge-plant-1", body.Data!.SiteId);
        Assert.Equal("Persisted", body.Data.Source);
        Assert.False(body.Data.ConfigPinned);
        Assert.False(body.Data.RestartRequired); // GET 反映当前生效态
    }

    [Fact]
    public void Site_Get_ConfiguredSource_MarksConfigPinned()
    {
        var provider = new FakeSiteIdProvider { Current = "env-site", Source = SiteIdSource.Configured };
        var ctrl = new SiteController(provider, NullLogger<SiteController>.Instance);

        var result = ctrl.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<SiteIdentityDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.True(body.Data!.ConfigPinned);
    }

    [Fact]
    public void Site_Update_Invalid_ReturnsBadRequest()
    {
        var provider = new FakeSiteIdProvider();
        var ctrl = new SiteController(provider, NullLogger<SiteController>.Instance);

        var result = ctrl.Update(new SiteIdentityUpdateRequest { SiteId = "Bad/Site" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("test-site", provider.Current); // 未生效
    }

    [Fact]
    public void Site_Update_Valid_PersistsAndReturnsRestartRequired()
    {
        var provider = new FakeSiteIdProvider();
        var ctrl = new SiteController(provider, NullLogger<SiteController>.Instance);

        var result = ctrl.Update(new SiteIdentityUpdateRequest { SiteId = "plant-b" });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<SiteIdentityDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("plant-b", body.Data!.SiteId);
        Assert.True(body.Data.RestartRequired);
        Assert.Equal("plant-b", provider.Current);
    }

    [Fact]
    public void Site_Regenerate_ReturnsNewIdAndRestartRequired()
    {
        var provider = new FakeSiteIdProvider { Current = "test-site" };
        var ctrl = new SiteController(provider, NullLogger<SiteController>.Instance);

        var result = ctrl.Regenerate();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<SiteIdentityDto>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal("test-site-2", body.Data!.SiteId);
        Assert.True(body.Data.RestartRequired);
        Assert.Equal("test-site-2", provider.Current);
    }
}
