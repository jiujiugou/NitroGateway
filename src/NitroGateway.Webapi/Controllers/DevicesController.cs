using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Deployment;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class DevicesController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IPointManager _points;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IProtocolDriverFactory _driverFactory;
    private readonly ISerialPortManager _serialPorts;
    private readonly DeploymentMode _deploymentMode;

    public DevicesController(
        IDeviceManager devices,
        IPointManager points,
        IDeviceHealthMonitor healthMonitor,
        IProtocolDriverFactory driverFactory,
        ISerialPortManager serialPorts,
        DeploymentMode deploymentMode)
    {
        _devices = devices;
        _points = points;
        _healthMonitor = healthMonitor;
        _driverFactory = driverFactory;
        _serialPorts = serialPorts;
        _deploymentMode = deploymentMode;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DeviceDto>>>> GetAll([FromQuery] string? siteId = null)
    {
        var r = await _devices.GetAllAsync(siteId);
        return r.IsSuccess ? Ok(ApiResponse<List<DeviceDto>>.Ok(r.Value!.Select(Map).ToList())) : NotFound(ApiResponse<List<DeviceDto>>.Fail("GetAll", r.Error!.Message));
    }

    /// <summary>
    /// 只读快照导出（ADR-033 阶段 2）：返回 devices+points 全量，供现场「从中心导入」使用。
    /// 与 GET /api/devices 同源（均含点位内联），JWT/RBAC 由类级 [Authorize] 覆盖；只读，不区分角色可写性。
    /// </summary>
    [HttpGet("export")]
    public async Task<ActionResult<ApiResponse<List<DeviceDto>>>> Export([FromQuery] string? siteId = null)
    {
        var r = await _devices.GetAllAsync(siteId);
        return r.IsSuccess ? Ok(ApiResponse<List<DeviceDto>>.Ok(r.Value!.Select(Map).ToList())) : NotFound(ApiResponse<List<DeviceDto>>.Fail("Export", r.Error!.Message));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Get(Guid id)
    {
        var r = await _devices.GetAsync(id);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : NotFound(ApiResponse<DeviceDto>.Fail("NotFound", r.Error!.Message));
    }

    [HttpPost]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Create(DeviceDto d)
    {
        var device = ToDomain(d);
        var r = await _devices.RegisterAsync(device);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : BadRequest(ApiResponse<DeviceDto>.Fail("Create", r.Error!.Message));
    }

    [HttpPut("{id}")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Update(Guid id, DeviceDto d)
    {
        var existing = await _devices.GetAsync(id);
        if (existing.IsFailure) return NotFound(ApiResponse<DeviceDto>.Fail("NotFound", "设备不存在"));
        var device = new Device { Id = id, Name = d.Name ?? "", Description = d.Description, Protocol = new ProtocolIdentifier { Name = d.Protocol?.Name ?? "", Dialect = d.Protocol?.Dialect }, Connection = BuildConnection(d.Connection), Status = Enum.TryParse<DeviceStatus>(d.Status, out var st2) ? st2 : DeviceStatus.Unknown, SiteId = d.SiteId ?? "" };
        var r = await _devices.RegisterAsync(device);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : BadRequest(ApiResponse<DeviceDto>.Fail("Update", r.Error!.Message));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        // ADR-033 阶段 3/4：中心删除=权威删除（tombstone 软删），同步下发驱动现场删除；
        // 现场上报不能复活（同步接收端拒绝 tombstone 设备的 upsert）
        var r = await _devices.SoftDeleteAsync(id);
        return r.IsSuccess ? Ok(ApiResponse<object>.Ok(new { })) : BadRequest(ApiResponse<object>.Fail("Delete", r.Error!.Message));
    }

    [HttpPut("{id}/status")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> UpdateStatus(Guid id, [FromBody] string status)
    {
        // ADR-022 P2-1：非法状态值返回 400，不再抛 FormatException 转 500
        if (!Enum.TryParse<DeviceStatus>(status, out var s))
            return BadRequest(ApiResponse<DeviceDto>.Fail("Status", $"无效的设备状态: {status}"));
        var r = await _devices.UpdateStatusAsync(id, s);
        if (r.IsFailure) return BadRequest(ApiResponse<DeviceDto>.Fail("Status", r.Error!.Message));
        _healthMonitor.UpdateStatus(id, s);
        var device = await _devices.GetAsync(id);
        return Ok(ApiResponse<DeviceDto>.Ok(Map(device.Value!)));
    }

    [HttpGet("{deviceId}/points")]
    public async Task<ActionResult<ApiResponse<List<PointDto>>>> GetPoints(Guid deviceId)
    {
        var r = await _points.GetByDeviceAsync(deviceId);
        return r.IsSuccess ? Ok(ApiResponse<List<PointDto>>.Ok(r.Value!.Select(MapPoint).ToList())) : BadRequest(ApiResponse<List<PointDto>>.Fail("GetPoints", r.Error!.Message));
    }

    [HttpPost("{deviceId}/points")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<PointDto>>> AddPoint(Guid deviceId, PointDto d)
    {
        // ADR-022 P2-1/P2-4：创建路径校验枚举并忽略客户端 ID（服务端生成，防 POST 覆盖既有点位）
        if (!Enum.TryParse<DataType>(d.DataType, out var dataType))
            return BadRequest(ApiResponse<PointDto>.Fail("AddPoint", $"无效的 DataType: {d.DataType}"));
        if (!Enum.TryParse<PointAccess>(d.Access, out var access))
            return BadRequest(ApiResponse<PointDto>.Fail("AddPoint", $"无效的 Access: {d.Access}"));
        var p = new DevicePoint { Id = Guid.NewGuid(), Name = d.Name ?? "", Address = d.Address ?? "", Description = d.Description, DataType = dataType, Access = access, Enabled = d.Enabled, ScanIntervalMs = d.ScanIntervalMs, Deadband = d.Deadband, ScaleFactor = d.ScaleFactor, ScaleOffset = d.ScaleOffset };
        var r = await _points.AddAsync(deviceId, p);
        return r.IsSuccess ? Ok(ApiResponse<PointDto>.Ok(MapPoint(r.Value!))) : BadRequest(ApiResponse<PointDto>.Fail("AddPoint", r.Error!.Message));
    }

    [HttpPut("{deviceId}/points/{pointId}")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<PointDto>>> UpdatePoint(Guid deviceId, Guid pointId, PointDto d)
    {
        // ADR-022 P2-1：非法枚举返回 400
        if (!Enum.TryParse<DataType>(d.DataType, out var dataType))
            return BadRequest(ApiResponse<PointDto>.Fail("UpdatePoint", $"无效的 DataType: {d.DataType}"));
        if (!Enum.TryParse<PointAccess>(d.Access, out var access))
            return BadRequest(ApiResponse<PointDto>.Fail("UpdatePoint", $"无效的 Access: {d.Access}"));
        var p = new DevicePoint { Id = pointId, Name = d.Name ?? "", Address = d.Address ?? "", Description = d.Description, DataType = dataType, Access = access, Enabled = d.Enabled, ScanIntervalMs = d.ScanIntervalMs, Deadband = d.Deadband, ScaleFactor = d.ScaleFactor, ScaleOffset = d.ScaleOffset };
        var r = await _points.UpdateAsync(deviceId, p);
        return r.IsSuccess ? Ok(ApiResponse<PointDto>.Ok(MapPoint(p))) : BadRequest(ApiResponse<PointDto>.Fail("UpdatePoint", r.Error!.Message));
    }

    [HttpDelete("{deviceId}/points/{pointId}")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<object>>> DeletePoint(Guid deviceId, Guid pointId)
    {
        var r = await _points.RemoveAsync(deviceId, pointId);
        return r.IsSuccess ? Ok(ApiResponse<object>.Ok(new { })) : BadRequest(ApiResponse<object>.Fail("DeletePoint", r.Error!.Message));
    }

    /// <summary>测试设备连接。前端保存前验证网络是否可达。</summary>
    [HttpPost("test-connection")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<object>>> TestConnection(DeviceDto d)
    {
        // ADR-044：连接测试是边缘物理操作，中心形态到不了现场 PLC，显式拒绝（不返回空/500）
        if (_deploymentMode == DeploymentMode.Center)
            return BadRequest(ApiResponse<object>.Fail("TestConnection", "中心形态无现场通路，请在桌面端测试连接"));

        if (d.Protocol is null || d.Connection is null)
            return Ok(ApiResponse<object>.Ok(new { success = false, latencyMs = 0L, error = "Protocol/Connection 不能为空" }));

        var protocol = new Domain.Devices.ProtocolIdentifier { Name = d.Protocol.Name ?? "", Dialect = d.Protocol.Dialect };
        // 连接测试不重试：RetryCount/RetryIntervalMs 置 0，避免失败重试拖长页面等待
        var connection = BuildConnection(d.Connection) with { RetryCount = 0, RetryIntervalMs = 0 };

        try
        {
            using var driver = _driverFactory.Create(protocol, connection);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await driver.ConnectAsync();

            if (result.IsSuccess)
            {
                // ADR-023：连接成功只代表链路/串口已通，不代表目标从站存在；
                // 必须 Ping（最小读请求）确认从站响应，否则测试结果对 UnitId 校验型从站是假阳性。
                var pingResult = await driver.PingAsync();
                sw.Stop();
                if (pingResult.IsSuccess)
                    return Ok(ApiResponse<object>.Ok(new
                    {
                        success = true,
                        latencyMs = sw.ElapsedMilliseconds,
                        ping = "ok"
                    }));

                return Ok(ApiResponse<object>.Ok(new { success = false, latencyMs = sw.ElapsedMilliseconds, error = pingResult.Error?.Message ?? "从站无响应" }));
            }

            sw.Stop();
            return Ok(ApiResponse<object>.Ok(new { success = false, latencyMs = sw.ElapsedMilliseconds, error = result.Error!.Message }));
        }
        catch (Exception ex)
        {
            return Ok(ApiResponse<object>.Ok(new { success = false, latencyMs = 0L, error = ex.Message }));
        }
    }

    /// <summary>列出系统可用串口（Windows COM / Linux tty 设备）</summary>
    [HttpGet("serial-ports")]
    public ActionResult<ApiResponse<List<string>>> GetSerialPorts()
    {
        if (_deploymentMode == DeploymentMode.Center)
            return BadRequest(ApiResponse<List<string>>.Fail("SerialPorts", "中心形态无现场串口，请到桌面端查看"));
        var ports = _serialPorts.GetAvailablePorts();
        return Ok(ApiResponse<List<string>>.Ok(ports.ToList()));
    }

    /// <summary>当前串口占用状态（端口、参数、租约数）</summary>
    [HttpGet("serial-port-status")]
    public ActionResult<ApiResponse<List<Protocols.Modbus.SerialPortInfo>>> GetSerialPortStatus()
    {
        if (_deploymentMode == DeploymentMode.Center)
            return BadRequest(ApiResponse<List<Protocols.Modbus.SerialPortInfo>>.Fail("SerialPortStatus", "中心形态无现场串口，请到桌面端查看"));
        var status = _serialPorts.GetStatus();
        return Ok(ApiResponse<List<Protocols.Modbus.SerialPortInfo>>.Ok(status.ToList()));
    }

    static DeviceDto Map(Device d) => new()
    {
        Id = d.Id.ToString(), Name = d.Name, Description = d.Description,
        Protocol = new ProtocolDto { Name = d.Protocol.Name, Dialect = d.Protocol.Dialect },
        Connection = new ConnectionDto { Endpoint = d.Connection.Endpoint, ConnectTimeoutMs = d.Connection.ConnectTimeoutMs, RequestTimeoutMs = d.Connection.RequestTimeoutMs, RetryCount = d.Connection.RetryCount, RetryIntervalMs = d.Connection.RetryIntervalMs, Parameters = d.Connection.Parameters },
        Status = d.Status.ToString(), SiteId = d.SiteId ?? "", Points = d.Points.Select(MapPoint).ToList(),
        UpdatedAt = d.UpdatedAt == default ? "" : d.UpdatedAt.ToUniversalTime().ToString("O"),
        IsDeleted = d.IsDeleted
    };
    static PointDto MapPoint(DevicePoint p) => new() { Id = p.Id.ToString(), Name = p.Name, Address = p.Address, Description = p.Description, DataType = p.DataType.ToString(), Access = p.Access.ToString(), Enabled = p.Enabled, ScanIntervalMs = p.ScanIntervalMs, Deadband = p.Deadband, ScaleFactor = p.ScaleFactor, ScaleOffset = p.ScaleOffset, UpdatedAt = p.UpdatedAt == default ? "" : p.UpdatedAt.ToUniversalTime().ToString("O"), IsDeleted = p.IsDeleted };
    // ADR-022 P2-4：创建路径一律服务端生成新 ID，忽略客户端传入的 Id（仓储 SaveAsync 为 upsert，防 POST 覆盖既有设备）
    static Device ToDomain(DeviceDto d) => new() { Id = Guid.NewGuid(), Name = d.Name ?? "", Description = d.Description, Protocol = new ProtocolIdentifier { Name = d.Protocol?.Name ?? "", Dialect = d.Protocol?.Dialect }, Connection = BuildConnection(d.Connection), SiteId = d.SiteId ?? "", Status = Enum.TryParse<DeviceStatus>(d.Status, out var st) ? st : DeviceStatus.Unknown };

    private static DeviceConnection BuildConnection(ConnectionDto? c) => c is null
        ? new DeviceConnection { Endpoint = "" }
        : new DeviceConnection { Endpoint = c.Endpoint ?? "", ConnectTimeoutMs = c.ConnectTimeoutMs, RequestTimeoutMs = c.RequestTimeoutMs, RetryCount = c.RetryCount, RetryIntervalMs = c.RetryIntervalMs, Parameters = c.Parameters };
}



