using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Protocols;
using NitroGateway.Security;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// OPC UA 服务器证书信任管理 API（ADR-073 D8 / P2-1c，AC-7）。
/// 前端证书面板：首连被拒（BadCertificateUntrusted）后列 rejected、一键信任（移入 trusted 白名单）、
/// 撤销信任。信任状态以 pki 目录为唯一权威；本 API 只操作文件系统，不入 SQLite 设备表。
/// 信任成功后可选 deviceId 触发该设备驱动驱逐 → 下一轮采集以新信任状态重连。
/// </summary>
[ApiController, Route("api/opcua/certificates")]
[Authorize(Roles = Roles.AdminOperator)]
public sealed class OpcUaCertificatesController : ControllerBase
{
    private readonly IOpcUaCertificateManager _certificates;
    private readonly IProtocolDriverPool _pool;
    private readonly ILogger<OpcUaCertificatesController> _logger;

    public OpcUaCertificatesController(
        IOpcUaCertificateManager certificates,
        IProtocolDriverPool pool,
        ILogger<OpcUaCertificatesController> logger)
    {
        _certificates = certificates;
        _pool = pool;
        _logger = logger;
    }

    /// <summary>列出入站被拒的服务器证书（首次连接未信任而被 SDK 写入 pki/rejected）。</summary>
    [HttpGet("rejected")]
    public ActionResult<ApiResponse<List<OpcUaCertificateDto>>> GetRejected()
        => Ok(ApiResponse<List<OpcUaCertificateDto>>.Ok(_certificates.GetRejected().ToList()));

    /// <summary>列出已信任的服务器证书白名单（pki/trusted）。</summary>
    [HttpGet("trusted")]
    public ActionResult<ApiResponse<List<OpcUaCertificateDto>>> GetTrusted()
        => Ok(ApiResponse<List<OpcUaCertificateDto>>.Ok(_certificates.GetTrusted().ToList()));

    /// <summary>
    /// 信任指定指纹的服务器证书：从 rejected 移入 trusted 白名单。
    /// 可选 <c>deviceId</c>：提供后驱逐该设备已缓存的（Faulted）驱动，使下一轮采集按新信任状态自动重连
    /// （ADR-073 D8「信任后触发该设备重试连接」）。
    /// </summary>
    [HttpPost("{thumbprint}/trust")]
    public async Task<ActionResult<ApiResponse<object>>> Trust(
        string thumbprint, [FromQuery] Guid? deviceId = null, CancellationToken ct = default)
    {
        var result = _certificates.Trust(thumbprint);
        if (result.IsFailure)
            return result.Error!.Code == "NotFound"
                ? NotFound(ApiResponse<object>.Fail("NotFound", result.Error.Message))
                : BadRequest(ApiResponse<object>.Fail("Trust", result.Error.Message));

        // 信任成功后按需驱逐设备驱动，令其在下一轮以信任证书重连（错误响应由调用方展示）
        if (deviceId.HasValue)
        {
            _pool.Evict(deviceId.Value);
            _logger.LogInformation("已信任 OPC UA 证书 {Thumbprint} 并触发设备 {DeviceId} 重连",
                thumbprint, deviceId.Value);
        }
        return Ok(ApiResponse<object>.Ok(new { trusted = thumbprint }));
    }

    /// <summary>撤销信任（运维）：把 trusted 白名单中的证书移除，使其回到未信任状态。</summary>
    [HttpDelete("{thumbprint}")]
    public ActionResult<ApiResponse<object>> Revoke(string thumbprint)
    {
        var result = _certificates.Revoke(thumbprint);
        if (result.IsFailure)
            return result.Error!.Code == "NotFound"
                ? NotFound(ApiResponse<object>.Fail("NotFound", result.Error.Message))
                : BadRequest(ApiResponse<object>.Fail("Revoke", result.Error.Message));
        return Ok(ApiResponse<object>.Ok(new { revoked = thumbprint }));
    }
}
