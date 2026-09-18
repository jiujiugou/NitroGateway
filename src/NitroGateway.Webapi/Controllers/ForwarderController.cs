using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Storage.Buffer;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class ForwarderController : ControllerBase
{
    private readonly IForwardMqttToggle _toggle;

    /// <param name="toggle">MQTT 转发总开关（内存态 + app_meta 持久化）</param>
    public ForwarderController(IForwardMqttToggle toggle) => _toggle = toggle;

    /// <summary>查询当前 MQTT 上云转发是否启用（只读，全员可访问）</summary>
    [HttpGet("enabled")]
    public ActionResult<ApiResponse<ForwardMqttEnabledDto>> GetEnabled()
        => Ok(ApiResponse<ForwardMqttEnabledDto>.Ok(new ForwardMqttEnabledDto { Enabled = _toggle.IsEnabled }));

    /// <summary>
    /// 设置 MQTT 上云转发开关（立即生效，持久化 app_meta；仅 Admin/Operator）。
    /// 失败（如持久化落库异常）返回 400，且内存态不变（开关保持原值）。
    /// </summary>
    /// <param name="request">目标状态</param>
    /// <param name="ct">取消令牌</param>
    [HttpPut("enabled")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<ForwardMqttEnabledDto>>> SetEnabled(
        [FromBody] ForwardMqttEnabledDto request, CancellationToken ct)
    {
        var result = await _toggle.SetEnabledAsync(request.Enabled, ct);
        if (result.IsFailure)
            return BadRequest(ApiResponse<ForwardMqttEnabledDto>.Fail("ForwarderToggle", result.Error!.Message));

        return Ok(ApiResponse<ForwardMqttEnabledDto>.Ok(new ForwardMqttEnabledDto { Enabled = _toggle.IsEnabled }));
    }
}
