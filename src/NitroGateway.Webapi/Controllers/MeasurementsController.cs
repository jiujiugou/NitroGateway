using Microsoft.AspNetCore.Mvc;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

[ApiController, Route("api/[controller]")]
public class MeasurementsController : ControllerBase
{
    private readonly IMeasurementStore _store;
    public MeasurementsController(IMeasurementStore store) => _store = store;

    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<List<MeasurementDto>>>> History([FromQuery] Guid deviceId, [FromQuery] Guid pointId, [FromQuery] DateTime from, [FromQuery] DateTime to)
    {
        var r = await _store.QueryAsync(deviceId, pointId, from, to);
        if (r.IsFailure) return BadRequest(ApiResponse<List<MeasurementDto>>.Fail("Query", r.Error!.Message));
        return Ok(ApiResponse<List<MeasurementDto>>.Ok(r.Value!.Select(s => new MeasurementDto { DeviceId = s.DeviceId.ToString(), DevicePointId = s.DevicePointId.ToString(), RawValue = s.RawValue, Value = s.Value, Timestamp = s.Timestamp.ToString("O"), Quality = s.Quality.ToString(), ErrorMessage = s.ErrorMessage }).ToList()));
    }

    [HttpGet("latest")]
    public async Task<ActionResult<ApiResponse<List<MeasurementDto>>>> Latest([FromQuery] Guid deviceId, [FromQuery] Guid pointId)
    {
        var now = DateTime.UtcNow;
        var r = await _store.QueryAsync(deviceId, pointId, now.AddHours(-1), now);
        if (r.IsFailure) return BadRequest(ApiResponse<List<MeasurementDto>>.Fail("Latest", r.Error!.Message));
        return Ok(ApiResponse<List<MeasurementDto>>.Ok(r.Value!.OrderByDescending(s => s.Timestamp).Take(1).Select(s => new MeasurementDto { DeviceId = s.DeviceId.ToString(), DevicePointId = s.DevicePointId.ToString(), RawValue = s.RawValue, Value = s.Value, Timestamp = s.Timestamp.ToString("O"), Quality = s.Quality.ToString(), ErrorMessage = s.ErrorMessage }).ToList()));
    }
}
