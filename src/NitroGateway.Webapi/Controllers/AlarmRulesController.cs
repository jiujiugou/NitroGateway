using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Alarm.Repository;
using AlarmDomain = NitroGateway.Alarm.Domain;
using NitroGateway.DeviceManagement;
using NitroGateway.Webapi.Models;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

/// <summary>告警规则管理 API</summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AdminOperator)]
public class AlarmRulesController : ControllerBase
{
    private readonly IAlarmRuleRepository _rules;

    public AlarmRulesController(IAlarmRuleRepository rules)
    {
        _rules = rules;
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
        // ADR-022 P2-1：非法 Guid/枚举返回 400，不再抛 FormatException 转 500
        if (!TryBuildRule(Guid.NewGuid(), d, out var rule, out var error))
            return BadRequest(ApiResponse<AlarmRuleDto>.Fail("Create", error));
        var r = await _rules.SaveAsync(rule);
        return r.IsSuccess
            ? Ok(ApiResponse<AlarmRuleDto>.Ok(Map(rule)))
            : BadRequest(ApiResponse<AlarmRuleDto>.Fail("Create", r.Error!.Message));
    }

    /// <summary>更新告警规则</summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<AlarmRuleDto>>> Update(Guid id, AlarmRuleDto d)
    {
        // ADR-022 P2-1：非法 Guid/枚举返回 400
        if (!TryBuildRule(id, d, out var rule, out var error))
            return BadRequest(ApiResponse<AlarmRuleDto>.Fail("Update", error));
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

    /// <summary>DTO → 领域模型；Guid/枚举解析失败返回 false + 错误文案（ADR-022 P2-1）</summary>
    private static bool TryBuildRule(Guid id, AlarmRuleDto d, out AlarmDomain.AlarmRule rule, out string error)
    {
        if (!Guid.TryParse(d.DeviceId, out var deviceId))
        {
            rule = null!;
            error = $"无效的 DeviceId: {d.DeviceId}";
            return false;
        }
        if (!Guid.TryParse(d.PointId, out var pointId))
        {
            rule = null!;
            error = $"无效的 PointId: {d.PointId}";
            return false;
        }
        if (!Enum.TryParse<AlarmDomain.AlarmSeverity>(d.Severity, out var severity))
        {
            rule = null!;
            error = $"无效的 Severity: {d.Severity}";
            return false;
        }

        rule = new AlarmDomain.AlarmRule
        {
            Id = id,
            DeviceId = deviceId,
            PointId = pointId,
            Operator = d.Operator,
            Threshold = d.Threshold,
            ThresholdUpper = d.ThresholdUpper,
            DurationSeconds = d.DurationSeconds,
            Severity = severity,
            MessageTemplate = d.MessageTemplate,
            Enabled = d.Enabled
        };
        error = "";
        return true;
    }
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
