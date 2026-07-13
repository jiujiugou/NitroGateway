using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Viewer")]
public class StatusController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly IDeviceHealthMonitor _healthMonitor;
    private readonly IForwardBuffer _buffer;
    private readonly IMqttClient _mqtt;
    private readonly ForwardingThrottle _throttle;
    private readonly ICircuitBreakerRegistry _breakers;

    public StatusController(
        IDeviceManager devices,
        IDeviceHealthMonitor healthMonitor,
        IForwardBuffer buffer,
        IMqttClient mqtt,
        ForwardingThrottle throttle,
        ICircuitBreakerRegistry breakers)
    {
        _devices = devices;
        _healthMonitor = healthMonitor;
        _buffer = buffer;
        _mqtt = mqtt;
        _throttle = throttle;
        _breakers = breakers;
    }

    /// <summary>设备状态概览</summary>
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

    /// <summary>设备健康详情</summary>
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

    /// <summary>系统状态面板（完整聚合）</summary>
    [HttpGet("system")]
    public ActionResult<ApiResponse<object>> SystemStatus()
    {
        var breakerStates = _breakers.GetAll().Select(kv => new
        {
            DeviceId = kv.Key.ToString(),
            State = kv.Value.State.ToString(),
            IsOpen = kv.Value.IsOpen
        }).ToList();

        return Ok(ApiResponse<object>.Ok(new
        {
            MqttState = _mqtt.State.ToString(),
            MqttConnected = _mqtt.State == MqttConnectionState.Connected,
            BufferBacklog = _buffer.Count,
            ThrottleBatchSize = _throttle.MaxBatchSize,
            ThrottleDelayMs = _throttle.DelayMs,
            OnlineDevices = _devices.GetByStatusAsync(Domain.Devices.DeviceStatus.Online).Result.IsSuccess
                ? _devices.GetByStatusAsync(Domain.Devices.DeviceStatus.Online).Result.Value!.Count
                : 0,
            CircuitBreakers = breakerStates
        }));
    }
}
