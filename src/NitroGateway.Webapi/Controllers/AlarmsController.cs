using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Repository;
using AlarmDomain = NitroGateway.Alarm.Domain;
using NitroGateway.Webapi.Models;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

/// <summary>告警管理 API</summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class AlarmsController : ControllerBase
{
    private readonly IAlarmRepository _alarms;

    public AlarmsController(IAlarmRepository alarms) { _alarms = alarms; }

    /// <summary>获取所有活跃告警</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> GetActive([FromQuery] string? siteId = null)
    {
        var r = await _alarms.GetAllActiveAsync(siteId);
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmDto>>.Fail("Alarms", r.Error!.Message));
    }

    /// <summary>
    /// 告警汇总（ADR-065 A1 仪表盘 KPI）：活跃告警数 + 今日发生告警数。
    /// 「今日」按服务器本地时区 0 点起算（网关运行在现场时区，本地语义最直观）。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<AlarmSummaryDto>>> Summary()
    {
        var activeResult = await _alarms.GetAllActiveAsync();
        if (activeResult.IsFailure)
            return BadRequest(ApiResponse<AlarmSummaryDto>.Fail("Alarms", activeResult.Error!.Message));

        // 本地时区今日 0 点 → UTC（存储为 UTC O 串，见 M005）
        var now = DateTime.Now;
        var localTodayStart = new DateTime(now.Year, now.Month, now.Day);
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localTodayStart);
        var todayResult = await _alarms.CountOccurredSinceAsync(todayStartUtc);
        if (todayResult.IsFailure)
            return BadRequest(ApiResponse<AlarmSummaryDto>.Fail("Alarms", todayResult.Error!.Message));

        return Ok(ApiResponse<AlarmSummaryDto>.Ok(new AlarmSummaryDto
        {
            Active = activeResult.Value!.Count,
            Today = todayResult.Value
        }));
    }

    /// <summary>获取指定设备的活跃告警</summary>
    [HttpGet("device/{deviceId}")]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> GetByDevice(Guid deviceId, [FromQuery] string? siteId = null)
    {
        var r = await _alarms.GetActiveByDeviceAsync(deviceId, siteId);
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmDto>>.Fail("Alarms", r.Error!.Message));
    }

    /// <summary>确认告警（操作员）</summary>
    [HttpPost("{alarmId}/ack")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<object>>> Acknowledge(Guid alarmId)
    {
        var r = await _alarms.UpdateStateAsync(alarmId, AlarmDomain.AlarmState.Acknowledged);
        return r.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("Ack", r.Error!.Message));
    }

    /// <summary>查询历史告警</summary>
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<List<AlarmDto>>>> History([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] string? siteId = null, [FromQuery] int limit = 1000)
    {
        // ADR-022 P2-2：limit 夹紧 1..1000，仓储层 Take 限制结果集，防大窗口全量内存
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var r = await _alarms.QueryAsync(from, to, siteId, safeLimit);
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

/// <summary>告警汇总 DTO（仪表盘 KPI：活跃数 / 今日发生数）</summary>
public sealed class AlarmSummaryDto
{
    /// <summary>当前活跃（含已确认未恢复）告警数</summary>
    public int Active { get; set; }

    /// <summary>今日（本地 0 点起）发生告警数</summary>
    public int Today { get; set; }
}
