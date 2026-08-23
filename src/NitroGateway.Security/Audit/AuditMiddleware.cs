using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Security.Audit;

/// <summary>
/// 审计中间件。拦截所有管理 API 调用，记录 Who/What/When/Result/IP。
/// 审计数据通过 ILogger 输出（结构化 JSON），由 Serilog Sink 落地。
/// <para>ADR-065 A3：非 GET /api/* 请求（写值/登录/配置变更）额外经 <see cref="IAuditLogStore"/>
/// 落 SQLite audit_logs 表，供操作日志查询页追溯；GET 高频轮询仅 Debug 日志不落库。
/// 落库为 best-effort——失败仅记日志，绝不阻断业务请求。</para>
/// </summary>
public sealed class AuditMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <param name="context">当前请求上下文</param>
    /// <param name="auditStore">审计落库存储（InvokeAsync 参数注入，支持 Scoped/Singleton 生命周期）</param>
    public async Task InvokeAsync(HttpContext context, IAuditLogStore auditStore)
    {
        var start = DateTime.UtcNow;

        await _next(context);

        // 只记录管理 API
        if (!context.Request.Path.StartsWithSegments("/api"))
            return;

        var user = context.User.FindFirst(ClaimTypes.Name)?.Value ?? "anonymous";
        var role = context.User.FindFirst(ClaimTypes.Role)?.Value ?? "-";
        var method = context.Request.Method;
        var path = context.Request.Path.ToString();
        var statusCode = context.Response.StatusCode;
        var elapsedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "-";

        // ADR-004 P3-3：刻意不记请求体——写类操作的变更内容属敏感数据，
        // 边缘网关适配范围按 method/path/status 审计即可；如需 body 摘要需先 EnableBuffering
        // ADR-022 P3-5：只读 GET 降 Debug（前端仪表盘 3-10s 轮询），避免高频轮询刷屏日志
        // ADR-065 A3：非 GET 落 audit_logs——写值/登录/配置变更正是审计查询页要追溯的操作
        if (HttpMethods.IsGet(context.Request.Method))
        {
            _logger.LogDebug(
                "AUDIT User={User} Role={Role} {Method} {Path} → {StatusCode} ({Elapsed}ms) IP={IP}",
                user, role, method, path, statusCode, elapsedMs, ip);
        }
        else if (statusCode >= 400)
        {
            _logger.LogWarning(
                "AUDIT User={User} Role={Role} {Method} {Path} → {StatusCode} ({Elapsed}ms) IP={IP}",
                user, role, method, path, statusCode, elapsedMs, ip);
        }
        else
        {
            _logger.LogInformation(
                "AUDIT User={User} Role={Role} {Method} {Path} → {StatusCode} ({Elapsed}ms) IP={IP}",
                user, role, method, path, statusCode, elapsedMs, ip);

            // best-effort 落库：审计失败（DB 忙/磁盘满等）绝不能拖垮写值/登录主流程
            try
            {
                await auditStore.WriteAsync(new AuditLogEntry
                {
                    User = user,
                    Role = role,
                    Method = method,
                    Path = path,
                    StatusCode = statusCode,
                    ElapsedMs = elapsedMs,
                    Ip = ip,
                    CreatedAt = DateTime.UtcNow
                }, context.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "审计落库异常（不影响请求）");
            }
        }
    }
}
