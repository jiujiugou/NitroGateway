using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Webapi.Models;

using NitroGateway.Security;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AllRoles)]
public class MeasurementsController : ControllerBase
{
    private readonly IMeasurementStore _store;
    public MeasurementsController(IMeasurementStore store) => _store = store;

    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<List<MeasurementDto>>>> History(
        [FromQuery] Guid deviceId, [FromQuery] Guid pointId, [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromQuery] string? siteId = null, [FromQuery] int limit = 1000, [FromQuery] int offset = 0)
    {
        // 避免大结果集一次性全量加载；默认 limit=1000 与旧行为接近，客户端可显式分页。
        var r = await _store.QueryPagedAsync(deviceId, pointId, from, to, limit, offset, siteId);
        if (r.IsFailure) return BadRequest(ApiResponse<List<MeasurementDto>>.Fail("History", r.Error!.Message));
        return Ok(ApiResponse<List<MeasurementDto>>.Ok(r.Value!.Select(s => new MeasurementDto { DeviceId = s.DeviceId.ToString(), DevicePointId = s.DevicePointId.ToString(), RawValue = s.RawValue, Value = s.Value, Timestamp = s.Timestamp.ToString("O"), Quality = s.Quality.ToString(), ErrorMessage = s.ErrorMessage }).ToList()));
    }

    [HttpGet("latest")]
    public async Task<ActionResult<ApiResponse<List<MeasurementDto>>>> Latest([FromQuery] Guid deviceId, [FromQuery] Guid pointId, [FromQuery] string? siteId = null)
    {
        var r = await _store.QueryLatestAsync(deviceId, pointId, siteId);
        if (r.IsFailure) return BadRequest(ApiResponse<List<MeasurementDto>>.Fail("Latest", r.Error!.Message));
        return Ok(ApiResponse<List<MeasurementDto>>.Ok(r.Value!.Select(s => new MeasurementDto { DeviceId = s.DeviceId.ToString(), DevicePointId = s.DevicePointId.ToString(), RawValue = s.RawValue, Value = s.Value, Timestamp = s.Timestamp.ToString("O"), Quality = s.Quality.ToString(), ErrorMessage = s.ErrorMessage }).ToList()));
    }

    /// <summary>获取设备所有点位的最新值（前端首屏渲染用）</summary>
    [HttpGet("latest-batch")]
    public async Task<ActionResult<ApiResponse<List<MeasurementDto>>>> LatestBatch([FromQuery] Guid deviceId, [FromQuery] string? siteId = null)
    {
        var r = await _store.QueryLatestAsync(deviceId, pointId: null, siteId);
        if (r.IsFailure) return BadRequest(ApiResponse<List<MeasurementDto>>.Fail("LatestBatch", r.Error!.Message));
        return Ok(ApiResponse<List<MeasurementDto>>.Ok(r.Value!
            .GroupBy(s => s.DevicePointId)
            .Select(g => g.OrderByDescending(s => s.Timestamp).First())
            .Select(s => new MeasurementDto { DeviceId = s.DeviceId.ToString(), DevicePointId = s.DevicePointId.ToString(), RawValue = s.RawValue, Value = s.Value, Timestamp = s.Timestamp.ToString("O"), Quality = s.Quality.ToString(), ErrorMessage = s.ErrorMessage })
            .ToList()));
    }
}
