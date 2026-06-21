using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IForwardBuffer? _buffer;
    private readonly IMqttClient? _mqtt;
    public StatusController(IDeviceManager devices, IForwardBuffer? buffer = null, IMqttClient? mqtt = null) { _devices = devices; _buffer = buffer; _mqtt = mqtt; }

    [HttpGet("devices")]
    public async Task<ActionResult<ApiResponse<List<DeviceStatusSummaryDto>>>> DeviceSummary()
    {
        var r = await _devices.GetAllAsync();
        if (r.IsFailure) return BadRequest(ApiResponse<List<DeviceStatusSummaryDto>>.Fail("Devices", r.Error!.Message));
        var summaries = r.Value!.Select(d => new DeviceStatusSummaryDto { DeviceId = d.Id.ToString(), DeviceName = d.Name, Status = d.Status.ToString() }).ToList();
        return Ok(ApiResponse<List<DeviceStatusSummaryDto>>.Ok(summaries));
    }

    [HttpGet("system")]
    public ActionResult<ApiResponse<object>> SystemStatus()
    {
        return Ok(ApiResponse<object>.Ok(new { BufferBacklog = _buffer?.Count ?? 0, MqttConnected = _mqtt?.State == MqttConnectionState.Connected }));
    }
}
