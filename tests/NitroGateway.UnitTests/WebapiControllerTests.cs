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
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Deployment;
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
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);
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
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);
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
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);

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
        var ctrl = new DevicesController(devices, new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);
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


    public async Task Sites_GetSites_ReturnsCatalogList()
    {
        // ADR-035 第 1 步 Web 维度：站点目录仅读接口，返回中心库去重后的 site 列表
        var catalog = new FakeSiteCatalog(new[] { "site-a", "site-b" });
        var ctrl = new SitesController(catalog);

        var result = await ctrl.GetSites();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<string>>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(new[] { "site-a", "site-b" }, body.Data);
        Assert.Equal(1, catalog.CallCount);
    }

    [Fact]
    public async Task Sites_GetSites_EmptyCatalog_ReturnsEmptyList()
    {
        var ctrl = new SitesController(new FakeSiteCatalog(Array.Empty<string>()));

        var result = await ctrl.GetSites();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<string>>>(ok.Value);
        Assert.Empty(body.Data);
    }
    [Fact]
    public async Task Sites_GetSiteInfos_ReturnsCatalogInfos()
    {
        // ADR-036 中心站点管理：详情列表含显示名/来源指纹/冲突标记
        var catalog = new FakeSiteCatalog(Array.Empty<string>())
        {
            SiteInfos = new[]
            {
                new SiteInfo { SiteId = "site-a", DisplayName = "一号站", HasConflict = true },
                new SiteInfo { SiteId = "site-b" }
            }
        };
        var ctrl = new SitesController(catalog);

        var result = await ctrl.GetSiteInfos();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<SiteInfo>>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(2, body.Data!.Count);
        Assert.Equal("一号站", body.Data[0].DisplayName);
        Assert.True(body.Data[0].HasConflict);
        Assert.Equal(1, catalog.InfoCalls);
    }

    [Fact]
    public async Task Sites_Rename_Valid_ReturnsOkAndForwards()
    {
        var catalog = new FakeSiteCatalog(Array.Empty<string>());
        var ctrl = new SitesController(catalog);

        var result = await ctrl.Rename("site-a", new RenameSiteRequest { DisplayName = " 一号站 " });

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("site-a", catalog.RenameSiteId);
        Assert.Equal("一号站", catalog.RenameDisplayName);   // 前后空白被 Trim
        Assert.Equal(1, catalog.RenameCalls);
    }

    [Fact]
    public async Task Sites_Rename_EmptySiteId_ReturnsBadRequest()
    {
        var catalog = new FakeSiteCatalog(Array.Empty<string>());
        var ctrl = new SitesController(catalog);

        var result = await ctrl.Rename(" ", new RenameSiteRequest { DisplayName = "x" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, catalog.RenameCalls);
    }

    [Fact]
    public async Task Sites_Rename_DisplayNameTooLong_ReturnsBadRequest()
    {
        var catalog = new FakeSiteCatalog(Array.Empty<string>());
        var ctrl = new SitesController(catalog);

        var result = await ctrl.Rename("site-a", new RenameSiteRequest { DisplayName = new string('x', 101) });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, catalog.RenameCalls);
    }
    [Fact]
    public async Task Devices_UpdateStatus_InvalidEnum_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);

        var result = await ctrl.UpdateStatus(Guid.NewGuid(), "BogusStatus");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_InvalidDataType_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);
        var dto = new PointDto { DataType = "Bogus", Access = "ReadOnly" };

        var result = await ctrl.AddPoint(Guid.NewGuid(), dto);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Devices_AddPoint_IgnoresClientProvidedId()
    {
        var points = new FakePointManager();
        var ctrl = new DevicesController(new FakeDeviceManager(), points, new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Gateway);
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
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), DeploymentMode.Gateway);
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
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), DeploymentMode.Gateway);
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
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(driver), new FakeSerialPorts(), DeploymentMode.Gateway);
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

    // ────── ADR-044：center 形态边缘能力显式拒绝 ──────

    [Fact]
    public async Task Devices_TestConnection_CenterMode_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Center);

        var result = await ctrl.TestConnection(TestConnectionDto());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<object>>(bad.Value);
        Assert.Contains("桌面端", body.Error?.Message);
    }

    [Fact]
    public void Devices_GetSerialPorts_CenterMode_ReturnsBadRequest()
    {
        var ctrl = new DevicesController(new FakeDeviceManager(), new FakePointManager(), new FakeHealthMonitor(), new FakeDriverFactory(), new FakeSerialPorts(), DeploymentMode.Center);

        var result = ctrl.GetSerialPorts();

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task StatusController_System_CenterMode_NullDeps_DoesNotThrow()
    {
        // ADR-044：center 模式不注册 Forwarder/MQTT/Collection，采集侧依赖为 null；
        // /status/system 必须返回中心侧信息（mode）而不 DI 500。
        var ctrl = new StatusController(
            new FakeDeviceManager(), new FakeHealthMonitor(),
            buffer: null, mqtt: null, throttle: null, breakers: null,
            deploymentMode: DeploymentMode.Center);

        var result = await ctrl.SystemStatus();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<object>>(ok.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(body.Data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Center", doc.RootElement.GetProperty("Mode").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("BufferBacklog").GetInt32());
    }

    [Fact]
    public void StatusController_Info_ReturnsDeploymentMode()
    {
        var ctrl = new StatusController(
            new FakeDeviceManager(), new FakeHealthMonitor(),
            buffer: null, mqtt: null, throttle: null, breakers: null,
            deploymentMode: DeploymentMode.Gateway);

        var result = ctrl.Info();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<object>>(ok.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(body.Data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Gateway", doc.RootElement.GetProperty("mode").GetString());
    }
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

    /// <summary>GetAllAsync 返回值（ADR-033 导出测试用）；缺省空列表</summary>
    public IReadOnlyList<Device> AllDevices { get; set; } = Array.Empty<Device>();

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
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(AllDevices));

    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());

    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(AllDevices.ToList()));
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
        => GetAllAsync(ct);
    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
        => GetAllIncludingDeletedAsync(ct);
    public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Success(AllDevices.FirstOrDefault(d => d.Id == deviceId)));
    public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
    {
        var device = AllDevices.FirstOrDefault(d => d.Id == deviceId);
        if (device is not null)
        {
            device.IsDeleted = true;
            device.UpdatedAt = DateTime.UtcNow;
        }
        return Task.FromResult(OperationResult.Success());
    }
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

public sealed class FakeSiteCatalog : ISiteCatalog
{
    private readonly IReadOnlyList<string> _sites;

    public FakeSiteCatalog(IReadOnlyList<string> sites) => _sites = sites;

    public int CallCount { get; private set; }

    public Task<OperationResult<IReadOnlyList<string>>> GetSitesAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(OperationResult<IReadOnlyList<string>>.Success(_sites));
    }

    /// <summary>注册调用记录（ADR-036）</summary>
    public int RegisterCalls { get; private set; }
    public string? LastSiteId { get; private set; }
    public string? LastClientId { get; private set; }

    public Task<OperationResult> RegisterSiteAsync(string siteId, string? sourceClientId, CancellationToken ct = default)
    {
        RegisterCalls++;
        LastSiteId = siteId;
        LastClientId = sourceClientId;
        return Task.FromResult(OperationResult.Success());
    }

    /// <summary>站点详情（ADR-036）：可注入返回值，供控制器用例断言。</summary>
    public IReadOnlyList<SiteInfo> SiteInfos { get; set; } = Array.Empty<SiteInfo>();
    public int InfoCalls { get; private set; }
    public int RenameCalls { get; private set; }
    public string? RenameSiteId { get; private set; }
    public string? RenameDisplayName { get; private set; }

    public Task<OperationResult<IReadOnlyList<SiteInfo>>> GetSiteInfosAsync(CancellationToken ct = default)
    {
        InfoCalls++;
        return Task.FromResult(OperationResult<IReadOnlyList<SiteInfo>>.Success(SiteInfos));
    }

    public Task<OperationResult> RenameSiteAsync(string siteId, string displayName, CancellationToken ct = default)
    {
        RenameCalls++;
        RenameSiteId = siteId;
        RenameDisplayName = displayName;
        return Task.FromResult(OperationResult.Success());
    }
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

    public Task<OperationResult<IReadOnlyList<AlarmRuleDomain>>> GetAllIncludingDisabledAsync(CancellationToken ct = default)
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




