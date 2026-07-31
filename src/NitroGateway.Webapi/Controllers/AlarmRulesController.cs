using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Repository;
using AlarmDomain = NitroGateway.Alarm.Domain;
using NitroGateway.DeviceManagement;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>告警规则管理 API</summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class AlarmRulesController : ControllerBase
{
    private readonly IAlarmRuleRepository _rules;
    private readonly IDeviceManager _devices;

    public AlarmRulesController(IAlarmRuleRepository rules, IDeviceManager devices)
    {
        _rules = rules;
        _devices = devices;
    }

    /// <summary>获取所有告警规则</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<AlarmRuleDto>>>> GetAll()
    {
        var r = await _rules.GetAllAsync();
        return r.IsSuccess
            ? Ok(ApiResponse<List<AlarmRuleDto>>.Ok(r.Value!.Select(Map).ToList()))
            : BadRequest(ApiResponse<List<AlarmRuleDto>>.Fail("Rules", r.Error!.Message));
    }

    /// <summary>创建告警规则</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<AlarmRuleDto>>> Create(AlarmRuleDto d)
    {
        var rule = ToDomain(d);
        var r = await _rules.SaveAsync(rule);
        return r.IsSuccess
            ? Ok(ApiResponse<AlarmRuleDto>.Ok(Map(rule)))
            : BadRequest(ApiResponse<AlarmRuleDto>.Fail("Create", r.Error!.Message));
    }

    /// <summary>更新告警规则</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<AlarmRuleDto>>> Update(Guid id, AlarmRuleDto d)
    {
        var rule = new AlarmDomain.AlarmRule
        {
            Id = id,
            DeviceId = Guid.Parse(d.DeviceId),
            PointId = Guid.Parse(d.PointId),
            Operator = d.Operator,
            Threshold = d.Threshold,
            ThresholdUpper = d.ThresholdUpper,
            DurationSeconds = d.DurationSeconds,
            Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(d.Severity),
            MessageTemplate = d.MessageTemplate,
            Enabled = d.Enabled
        };
        var r = await _rules.SaveAsync(rule);
        return r.IsSuccess
            ? Ok(ApiResponse<AlarmRuleDto>.Ok(Map(rule)))
            : BadRequest(ApiResponse<AlarmRuleDto>.Fail("Update", r.Error!.Message));
    }

    /// <summary>删除告警规则</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid id)
    {
        var r = await _rules.DeleteAsync(id);
        return r.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("Delete", r.Error!.Message));
    }

    private static AlarmRuleDto Map(AlarmDomain.AlarmRule r) => new()
    {
        Id = r.Id.ToString(),
        DeviceId = r.DeviceId.ToString(),
        PointId = r.PointId.ToString(),
        Operator = r.Operator,
        Threshold = r.Threshold,
        ThresholdUpper = r.ThresholdUpper,
        DurationSeconds = r.DurationSeconds,
        Severity = r.Severity.ToString(),
        MessageTemplate = r.MessageTemplate,
        Enabled = r.Enabled
    };

    private static AlarmDomain.AlarmRule ToDomain(AlarmRuleDto d) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Guid.Parse(d.DeviceId),
        PointId = Guid.Parse(d.PointId),
        Operator = d.Operator,
        Threshold = d.Threshold,
        ThresholdUpper = d.ThresholdUpper,
        DurationSeconds = d.DurationSeconds,
        Severity = Enum.Parse<AlarmDomain.AlarmSeverity>(d.Severity),
        MessageTemplate = d.MessageTemplate,
        Enabled = d.Enabled
    };
}

/// <summary>告警规则 DTO</summary>
public class AlarmRuleDto
{
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string PointId { get; set; } = "";
    public string Operator { get; set; } = ">";
    public double Threshold { get; set; }
    public double? ThresholdUpper { get; set; }
    public int DurationSeconds { get; set; }
    public string Severity { get; set; } = "Warning";
    public string? MessageTemplate { get; set; }
    public bool Enabled { get; set; } = true;
}
