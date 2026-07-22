using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Repository;
using AlarmDomain = NitroGateway.Alarm.Domain;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>告警管理 API</summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Viewer")]
public class AlarmsController : ControllerBase
{
    private readonly IAlarmRepository _alarms;

    public AlarmsController(IAlarmRepository alarms) { _alarms = alarms; }

    /// <summary>获取所有活跃告警</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> GetActive()
    {
        var r = await _alarms.GetAllActiveAsync();
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmDto>>.Fail("Alarms", r.Error!.Message));
    }

    /// <summary>获取指定设备的活跃告警</summary>
    [HttpGet("device/{deviceId}")]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> GetByDevice(Guid deviceId)
    {
        var r = await _alarms.GetActiveByDeviceAsync(deviceId);
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmDto>>.Fail("Alarms", r.Error!.Message));
    }

    /// <summary>确认告警（操作员）</summary>
    [HttpPost("{alarmId}/ack")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<ApiResponse<object>>> Acknowledge(Guid alarmId)
    {
        var r = await _alarms.UpdateStateAsync(alarmId, AlarmDomain.AlarmState.Acknowledged);
        return r.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("Ack", r.Error!.Message));
    }

    /// <summary>查询历史告警</summary>
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> History([FromQuery] DateTime from, [FromQuery] DateTime to)
    {
        var r = await _alarms.QueryAsync(from, to);
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmDto>>.Fail("History", r.Error!.Message));
    }

    private static AlarmDto Map(AlarmDomain.Alarm a) => new()
    {
        Id = a.Id.ToString(),
        RuleId = a.RuleId.ToString(),
        DeviceId = a.DeviceId.ToString(),
        PointId = a.PointId.ToString(),
        TriggerValue = a.TriggerValue,
        Threshold = a.Threshold,
        Severity = a.Severity.ToString(),
        Message = a.Message,
        State = a.State.ToString(),
        OccurredAt = a.OccurredAt.ToString("O"),
        ResolvedAt = a.ResolvedAt?.ToString("O"),
        AcknowledgedAt = a.AcknowledgedAt?.ToString("O")
    };
}

/// <summary>告警 DTO</summary>
public class AlarmDto
{
    public string Id { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string PointId { get; set; } = "";
    public double TriggerValue { get; set; }
    public double Threshold { get; set; }
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string State { get; set; } = "";
    public string OccurredAt { get; set; } = "";
    public string? ResolvedAt { get; set; }
    public string? AcknowledgedAt { get; set; }
}
