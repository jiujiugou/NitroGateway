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
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IForwardBuffer? _buffer;
    private readonly IMqttClient? _mqtt;

    public StatusController(
        IDeviceManager devices,
        IDeviceHealthMonitor healthMonitor,
        IForwardBuffer? buffer = null,
        IMqttClient? mqtt = null)
    {
        _devices = devices;
        _healthMonitor = healthMonitor;
        _buffer = buffer;
        _mqtt = mqtt;
    }

    /// <summary>设备状态概览（来自 DeviceManager）</summary>
    [HttpGet("devices")]
    public async Task<ActionResult<ApiResponse<List<DeviceStatusSummaryDto>>>> DeviceSummary()
    {
        var r = await _devices.GetAllAsync();
        if (r.IsFailure)
            return BadRequest(ApiResponse<List<DeviceStatusSummaryDto>>.Fail("Devices", r.Error!.Message));

        var summaries = r.Value!.Select(d => new DeviceStatusSummaryDto
        {
            DeviceId = d.Id.ToString(),
            DeviceName = d.Name,
            Status = d.Status.ToString()
        }).ToList();

        return Ok(ApiResponse<List<DeviceStatusSummaryDto>>.Ok(summaries));
    }

    /// <summary>设备健康详情（来自 DeviceHealthMonitor，含采集统计）</summary>
    [HttpGet("devices/health")]
    public ActionResult<ApiResponse<List<DeviceHealthDto>>> DeviceHealth()
    {
        var snapshots = _healthMonitor.GetAllSnapshots();
        var items = snapshots.Select(s => new DeviceHealthDto
        {
            DeviceId = s.DeviceId.ToString(),
            Status = s.Status.ToString(),
            LastCollectionAt = s.LastCollectionAt?.ToString("O"),
            ConsecutiveFailures = s.ConsecutiveFailures,
            ConsecutiveSuccesses = s.ConsecutiveSuccesses,
            LastError = s.LastError
        }).ToList();

        return Ok(ApiResponse<List<DeviceHealthDto>>.Ok(items));
    }

    /// <summary>系统状态概览</summary>
    [HttpGet("system")]
    public ActionResult<ApiResponse<object>> SystemStatus()
    {
        return Ok(ApiResponse<object>.Ok(new
        {
            BufferBacklog = _buffer?.Count ?? 0,
            MqttConnected = _mqtt?.State == MqttConnectionState.Connected
        }));
    }
}
