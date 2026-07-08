using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Security.Audit;

/// <summary>
/// 审计中间件。拦截所有管理 API 调用，记录 Who/What/When/Result/IP。
/// 审计数据通过 ILogger 输出（结构化 JSON），由 Serilog Sink 落地。
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

    public async Task InvokeAsync(HttpContext context)
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

        if (statusCode >= 400)
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
        }
    }
}
