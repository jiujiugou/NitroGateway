using Microsoft.AspNetCore.Mvc;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>死信队列管理 API</summary>
[ApiController, Route("api/[controller]")]
public class DeadLettersController : ControllerBase
{
    private readonly IForwardBuffer _buffer;

    public DeadLettersController(IForwardBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>获取死信列表</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<DeadLetterDto>>>> GetAll([FromQuery] int maxCount = 100)
    {
        var result = await _buffer.GetDeadLettersAsync(maxCount);
        if (result.IsFailure)
            return BadRequest(ApiResponse<List<DeadLetterDto>>.Fail("GetDeadLetters", result.Error!.Message));

        var items = result.Value!.Select(d => new DeadLetterDto
        {
            BatchId = d.BatchId.ToString(),
            DeviceId = d.DeviceId.ToString(),
            RecordCount = d.RecordCount,
            RetryCount = d.RetryCount,
            LastError = d.LastError,
            EnqueuedAt = d.EnqueuedAt.ToString("O")
        }).ToList();

        return Ok(ApiResponse<List<DeadLetterDto>>.Ok(items));
    }

    /// <summary>重放死信（重新入队）</summary>
    [HttpPost("{batchId}/retry")]
    public async Task<ActionResult<ApiResponse<object>>> Retry(Guid batchId)
    {
        var result = await _buffer.RetryDeadLetterAsync(batchId);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("RetryDeadLetter", result.Error!.Message));
    }

    /// <summary>丢弃死信</summary>
    [HttpDelete("{batchId}")]
    public async Task<ActionResult<ApiResponse<object>>> Discard(Guid batchId)
    {
        var result = await _buffer.DiscardDeadLetterAsync(batchId);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { }))
            : BadRequest(ApiResponse<object>.Fail("DiscardDeadLetter", result.Error!.Message));
    }
}

/// <summary>死信条目 DTO</summary>
public class DeadLetterDto
{
    public string BatchId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int RecordCount { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string EnqueuedAt { get; set; } = "";
}
