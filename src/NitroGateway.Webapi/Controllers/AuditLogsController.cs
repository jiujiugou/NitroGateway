using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NitroGateway.Security;
using NitroGateway.Security.Audit;
using NitroGateway.Webapi.Models;

namespace NitroGateway.Webapi.Controllers;

/// <summary>
/// 操作审计日志查询 API（ADR-065 A3）。数据源为 SQLite audit_logs（AuditMiddleware 非 GET /api/* 落库），
/// 把「写值 → 审计 → 可追溯」闭环可视化。审计属敏感数据，仅 Admin/Operator 可查。
/// </summary>
[ApiController, Route("api/[controller]")]
[Authorize(Roles = Roles.AdminOperator)]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditLogStore _store;

    /// <param name="store">审计存储（SQLite 实现，Dapper 单例）</param>
    public AuditLogsController(IAuditLogStore store) => _store = store;

    /// <summary>
    /// 分页查询操作审计日志（时间倒序）。支持 时间/操作者/方法/路径包含/状态码 过滤。
    /// page 从 1 起，pageSize 夹紧 1..200。
    /// </summary>
    /// <param name="from">起始时间（UTC，含）</param>
    /// <param name="to">结束时间（UTC，含）</param>
    /// <param name="user">操作者精确匹配</param>
    /// <param name="method">HTTP 方法精确匹配（POST/PUT/DELETE/PATCH）</param>
    /// <param name="path">路径包含匹配</param>
    /// <param name="status">状态码精确匹配</param>
    /// <param name="page">页码（默认 1）</param>
    /// <param name="pageSize">每页条数（默认 50，夹紧 1..200）</param>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AuditLogPageDto>>> Query(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? user = null,
        [FromQuery] string? method = null,
        [FromQuery] string? path = null,
        [FromQuery] int? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var result = await _store.QueryAsync(new AuditLogQuery
        {
            From = from,
            To = to,
            User = user,
            Method = method,
            PathContains = path,
            StatusCode = status,
            Page = page,
            PageSize = pageSize
        }, HttpContext.RequestAborted);

        if (result.IsFailure)
            return BadRequest(ApiResponse<AuditLogPageDto>.Fail("AuditLogs", result.Error!.Message));

        return Ok(ApiResponse<AuditLogPageDto>.Ok(new AuditLogPageDto
        {
            Items = result.Value!.Items.Select(Map).ToList(),
            Total = result.Value.Total,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 200)
        }));
    }

    private static AuditLogDto Map(AuditLogEntry e) => new()
    {
        Id = e.Id.ToString(),
        User = e.User,
        Role = e.Role,
        Method = e.Method,
        Path = e.Path,
        StatusCode = e.StatusCode,
        ElapsedMs = e.ElapsedMs,
        Ip = e.Ip,
        CreatedAt = e.CreatedAt.ToString("O")
    };
}

/// <summary>审计记录 DTO（查询页展示）</summary>
public sealed class AuditLogDto
{
    public string Id { get; set; } = "";
    public string User { get; set; } = "";
    public string Role { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int StatusCode { get; set; }
    public int ElapsedMs { get; set; }
    public string Ip { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

/// <summary>审计分页结果 DTO</summary>
public sealed class AuditLogPageDto
{
    public List<AuditLogDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
