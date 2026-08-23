using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Security;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 写端点（docs/14 §3.3）：下发控制指令到设备点位。Admin/Operator 可写，Viewer 只读。
/// 真正的校验（Access + WriteGuard 三级门控）与写执行在 <see cref="IWriteService"/>（Device 模块），
/// 本控制器只负责 HTTP 绑定与结果封装。
/// </summary>
[ApiController, Route("api/devices")]
[Authorize(Roles = Roles.AdminOperator)]
public sealed class WriteController : ControllerBase
{
    private readonly IWriteService _writeService;

    public WriteController(IWriteService writeService) => _writeService = writeService;

    /// <summary>写值请求体：<c>{ "value": 123 }</c>（JSON number/string/bool 均可，按点位 DataType 转换）</summary>
    public sealed class WriteValueRequest
    {
        /// <summary>写入值（原始 JSON 元素，服务层按点位 DataType 统一转换）</summary>
        public JsonElement? Value { get; set; }
    }

    /// <summary>
    /// 下发单点写命令。成功返回 200 + <see cref="ApiResponse{T}"/>；校验/驱动失败返回 400，Message 携带原因。
    /// </summary>
    [HttpPost("{deviceId:guid}/points/{pointId:guid}/write")]
    public async Task<IActionResult> Write(
        Guid deviceId, Guid pointId, [FromBody] WriteValueRequest? body, CancellationToken ct)
    {
        if (body?.Value is null || body.Value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return BadRequest(ApiResponse<object>.Fail("Write", "缺少 value 字段"));

        var result = await _writeService.WriteAsync(new WriteRequest
        {
            DeviceId = deviceId,
            PointId = pointId,
            Value = body.Value.Value
        }, ct);

        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new
            {
                deviceId = deviceId.ToString(),
                pointId = pointId.ToString(),
                value = body.Value.Value
            }))
            : BadRequest(ApiResponse<object>.Fail("Write", result.Error!.Message));
    }
}
