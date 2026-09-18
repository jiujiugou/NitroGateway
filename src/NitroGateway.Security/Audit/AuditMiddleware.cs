using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Security.Audit;

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
