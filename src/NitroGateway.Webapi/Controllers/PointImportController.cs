using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>点位批量导入/导出 API</summary>
[ApiController, Route("api/devices/{deviceId}/points")]
[Authorize(Roles = "Admin,Operator")]
public class PointImportController : ControllerBase
{
    private readonly IPointManager _points;
    private readonly PointBatchService _batch;

    public PointImportController(IPointManager points, PointBatchService batch)
    {
        _points = points;
        _batch = batch;
    }

    /// <summary>CSV 导入点位（POST body 为 CSV 文本）</summary>
    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<object>>> ImportCsv(
        Guid deviceId,
        [FromBody] string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
            return BadRequest(ApiResponse<object>.Fail("Import", "CSV 内容为空"));

        var parseResult = _batch.ParseCsv(csvText);
        if (parseResult.IsFailure)
            return BadRequest(ApiResponse<object>.Fail("Import", parseResult.Error!.Message));

        var importResult = await _points.ImportAsync(deviceId, parseResult.Value!);
        if (importResult.IsFailure)
            return BadRequest(ApiResponse<object>.Fail("Import", importResult.Error!.Message));

        return Ok(ApiResponse<object>.Ok(new { Count = importResult.Value!.Count }));
    }

    /// <summary>批量生成点位（地址自动递增 + 名称模板）</summary>
    [HttpPost("generate")]
    public async Task<ActionResult<ApiResponse<object>>> Generate(
        Guid deviceId,
        [FromBody] GenerateRequest req)
    {
        if (req.Count <= 0 || req.Count > 5000)
            return BadRequest(ApiResponse<object>.Fail("Generate", "Count 需在 1-5000 之间"));

        if (!Enum.TryParse<DataType>(req.DataType, true, out var dataType))
            return BadRequest(ApiResponse<object>.Fail("Generate", $"无效的 DataType: {req.DataType}"));

        var access = Enum.TryParse<PointAccess>(req.Access, true, out var acc) ? acc : PointAccess.ReadOnly;

        var points = _batch.Generate(deviceId, req.NameTemplate, req.StartAddress, req.Count, dataType, access);
        var result = await _points.ImportAsync(deviceId, points);

        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { Count = result.Value!.Count }))
            : BadRequest(ApiResponse<object>.Fail("Generate", result.Error!.Message));
    }

    /// <summary>导出设备所有点位为 CSV</summary>
    [HttpGet("export")]
    public async Task<ActionResult> ExportCsv(Guid deviceId)
    {
        var result = await _points.GetByDeviceAsync(deviceId);
        if (result.IsFailure)
            return BadRequest(ApiResponse<object>.Fail("Export", result.Error!.Message));

        var csv = _batch.ExportCsv(result.Value!);
        return File(
            System.Text.Encoding.UTF8.GetBytes(csv),
            "text/csv",
            $"points_{deviceId}.csv");
    }
}

/// <summary>批量生成请求</summary>
public class GenerateRequest
{
    /// <summary>名称模板，如 "AI_{###}" → AI_001, AI_002...</summary>
    public string NameTemplate { get; set; } = "Point_{###}";

    /// <summary>起始地址（整数，如 Modbus 40001）</summary>
    public int StartAddress { get; set; }

    /// <summary>生成数量</summary>
    public int Count { get; set; }

    /// <summary>数据类型字符串</summary>
    public string DataType { get; set; } = "Float";

    /// <summary>读写权限字符串</summary>
    public string Access { get; set; } = "ReadOnly";
}
