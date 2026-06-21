using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IPointManager _points;
    public DevicesController(IDeviceManager devices, IPointManager points) { _devices = devices; _points = points; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DeviceDto>>>> GetAll()
    {
        var r = await _devices.GetAllAsync();
        return r.IsSuccess ? Ok(ApiResponse<List<DeviceDto>>.Ok(r.Value!.Select(Map).ToList())) : NotFound(ApiResponse<List<DeviceDto>>.Fail("GetAll", r.Error!.Message));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Get(Guid id)
    {
        var r = await _devices.GetAsync(id);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : NotFound(ApiResponse<DeviceDto>.Fail("NotFound", r.Error!.Message));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Create(DeviceDto d)
    {
        var device = ToDomain(d);
        var r = await _devices.RegisterAsync(device);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : BadRequest(ApiResponse<DeviceDto>.Fail("Create", r.Error!.Message));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> Update(Guid id, DeviceDto d)
    {
        var existing = await _devices.GetAsync(id);
        if (existing.IsFailure) return NotFound(ApiResponse<DeviceDto>.Fail("NotFound", "设备不存在"));
        var device = ToDomain(d) with { Id = id };
        var r = await _devices.RegisterAsync(device);
        return r.IsSuccess ? Ok(ApiResponse<DeviceDto>.Ok(Map(r.Value!))) : BadRequest(ApiResponse<DeviceDto>.Fail("Update", r.Error!.Message));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var r = await _devices.UnregisterAsync(id);
        return r.IsSuccess ? Ok(ApiResponse<object>.Ok(new { })) : BadRequest(ApiResponse<object>.Fail("Delete", r.Error!.Message));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<ApiResponse<DeviceDto>>> UpdateStatus(Guid id, [FromBody] string status)
    {
        var s = Enum.Parse<DeviceStatus>(status);
        var r = await _devices.UpdateStatusAsync(id, s);
        if (r.IsFailure) return BadRequest(ApiResponse<DeviceDto>.Fail("Status", r.Error!.Message));
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
    public async Task<ActionResult<ApiResponse<PointDto>>> AddPoint(Guid deviceId, PointDto d)
    {
        var p = ToDomainPoint(d);
        var r = await _points.AddAsync(deviceId, p);
        return r.IsSuccess ? Ok(ApiResponse<PointDto>.Ok(MapPoint(r.Value!))) : BadRequest(ApiResponse<PointDto>.Fail("AddPoint", r.Error!.Message));
    }

    [HttpPut("{deviceId}/points/{pointId}")]
    public async Task<ActionResult<ApiResponse<PointDto>>> UpdatePoint(Guid deviceId, Guid pointId, PointDto d)
    {
        var p = ToDomainPoint(d) with { Id = pointId };
        var r = await _points.UpdateAsync(deviceId, p);
        return r.IsSuccess ? Ok(ApiResponse<PointDto>.Ok(MapPoint(p))) : BadRequest(ApiResponse<PointDto>.Fail("UpdatePoint", r.Error!.Message));
    }

    [HttpDelete("{deviceId}/points/{pointId}")]
    public async Task<ActionResult<ApiResponse<object>>> DeletePoint(Guid deviceId, Guid pointId)
    {
        var r = await _points.RemoveAsync(deviceId, pointId);
        return r.IsSuccess ? Ok(ApiResponse<object>.Ok(new { })) : BadRequest(ApiResponse<object>.Fail("DeletePoint", r.Error!.Message));
    }

    static DeviceDto Map(Device d) => new()
    {
        Id = d.Id.ToString(), Name = d.Name, Description = d.Description,
        Protocol = new ProtocolDto { Name = d.Protocol.Name, Dialect = d.Protocol.Dialect },
        Connection = new ConnectionDto { Endpoint = d.Connection.Endpoint, ConnectTimeoutMs = d.Connection.ConnectTimeoutMs, RequestTimeoutMs = d.Connection.RequestTimeoutMs, RetryCount = d.Connection.RetryCount, RetryIntervalMs = d.Connection.RetryIntervalMs, Parameters = d.Connection.Parameters },
        Status = d.Status.ToString(), Points = d.Points.Select(MapPoint).ToList()
    };
    static PointDto MapPoint(DevicePoint p) => new() { Id = p.Id.ToString(), Name = p.Name, Address = p.Address, Description = p.Description, DataType = p.DataType.ToString(), Access = p.Access.ToString(), Enabled = p.Enabled, ScanIntervalMs = p.ScanIntervalMs, Deadband = p.Deadband, ScaleFactor = p.ScaleFactor, ScaleOffset = p.ScaleOffset };
    static Device ToDomain(DeviceDto d) => new() { Id = string.IsNullOrEmpty(d.Id) ? Guid.NewGuid() : Guid.Parse(d.Id), Name = d.Name, Description = d.Description, Protocol = new ProtocolIdentifier { Name = d.Protocol.Name, Dialect = d.Protocol.Dialect }, Connection = new DeviceConnection { Endpoint = d.Connection.Endpoint, ConnectTimeoutMs = d.Connection.ConnectTimeoutMs, RequestTimeoutMs = d.Connection.RequestTimeoutMs, RetryCount = d.Connection.RetryCount, RetryIntervalMs = d.Connection.RetryIntervalMs, Parameters = d.Connection.Parameters }, Status = Enum.TryParse<DeviceStatus>(d.Status, out var st) ? st : DeviceStatus.Unknown };
    static DevicePoint ToDomainPoint(PointDto d) => new() { Id = string.IsNullOrEmpty(d.Id) ? Guid.NewGuid() : Guid.Parse(d.Id), Name = d.Name, Address = d.Address, Description = d.Description, DataType = Enum.Parse<DataType>(d.DataType), Access = Enum.Parse<PointAccess>(d.Access), Enabled = d.Enabled, ScanIntervalMs = d.ScanIntervalMs, Deadband = d.Deadband, ScaleFactor = d.ScaleFactor, ScaleOffset = d.ScaleOffset };
}
