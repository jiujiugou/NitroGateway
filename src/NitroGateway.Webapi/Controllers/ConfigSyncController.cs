using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;
using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 配置同步 API（ADR-033 阶段 3/4）：中心导出全量快照（含 tombstone）供现场合并下发；
/// 现场把离线改动上报到中心，按 UpdatedAt 双向合并、中心为最终裁决。
/// </summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class ConfigSyncController : ControllerBase
{
    private readonly IDeviceManager _devices;
    private readonly ConfigSyncService _sync;

    public ConfigSyncController(IDeviceManager devices, ConfigSyncService sync)
    {
        _devices = devices;
        _sync = sync;
    }

    /// <summary>
    /// 同步导出（只读）：全量设备（含 tombstone 与点位）+ 中心服务器时间。
    /// 现场按 UpdatedAt 双向合并：中心较新覆盖本地、中心 tombstone 驱动本地删除。
    /// </summary>
    [HttpGet("export")]
    public async Task<ActionResult<ApiResponse<ConfigSyncExportDto>>> Export([FromQuery] string? siteId = null)
    {
        var r = await _devices.GetAllIncludingDeletedAsync(siteId);
        if (r.IsFailure)
            return BadRequest(ApiResponse<ConfigSyncExportDto>.Fail("Export", r.Error!.Message));

        return Ok(ApiResponse<ConfigSyncExportDto>.Ok(new ConfigSyncExportDto
        {
            ServerTime = DateTime.UtcNow.ToString("O"),
            Devices = r.Value!.Select(Map).ToList()
        }));
    }

    /// <summary>
    /// 同步上报（写）：现场离线改动（upsert + tombstone）按 UpdatedAt 合并到中心。
    /// 返回逐台设备结论：accepted / skipped（中心较新）/ rejected（中心已删拒绝复活）。
    /// </summary>
    [HttpPost("push")]
    [Authorize(Roles = Roles.AdminOperator)]
    public async Task<ActionResult<ApiResponse<ConfigSyncPushResultDto>>> Push(ConfigSyncPushRequest request)
    {
        if (request.Changes.Count == 0)
            return Ok(ApiResponse<ConfigSyncPushResultDto>.Ok(new ConfigSyncPushResultDto()));

        var result = await _sync.ApplyAsync(request);
        return Ok(ApiResponse<ConfigSyncPushResultDto>.Ok(result));
    }

    private static DeviceDto Map(Device d) => new()
    {
        Id = d.Id.ToString(), Name = d.Name, Description = d.Description,
        Protocol = new ProtocolDto { Name = d.Protocol.Name, Dialect = d.Protocol.Dialect },
        Connection = new ConnectionDto
        {
            Endpoint = d.Connection.Endpoint,
            ConnectTimeoutMs = d.Connection.ConnectTimeoutMs,
            RequestTimeoutMs = d.Connection.RequestTimeoutMs,
            RetryCount = d.Connection.RetryCount,
            RetryIntervalMs = d.Connection.RetryIntervalMs,
            Parameters = d.Connection.Parameters
        },
        Status = d.Status.ToString(),
        UpdatedAt = d.UpdatedAt == default ? "" : d.UpdatedAt.ToUniversalTime().ToString("O"),
        IsDeleted = d.IsDeleted,
        Points = d.Points.Select(p => new PointDto
        {
            Id = p.Id.ToString(), Name = p.Name, Address = p.Address, Description = p.Description,
            DataType = p.DataType.ToString(), Access = p.Access.ToString(), Enabled = p.Enabled,
            ScanIntervalMs = p.ScanIntervalMs, Deadband = p.Deadband, ScaleFactor = p.ScaleFactor,
            ScaleOffset = p.ScaleOffset,
            UpdatedAt = p.UpdatedAt == default ? "" : p.UpdatedAt.ToUniversalTime().ToString("O"),
            IsDeleted = p.IsDeleted
        }).ToList()
    };
}

