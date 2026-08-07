using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Security.Audit;

/// <summary>
/// 未处理异常兜底中间件（ADR-004 P2-4）。
/// 必须注册在 AuditMiddleware 内层（后于其注册）：端点抛异常时先在此统一转 500 响应，
/// 再让外层的 AuditMiddleware 记录到真实状态码，避免审计丢失。
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "未处理异常: {Method} {Path}", context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                error = new { code = "InternalError", message = "服务器内部错误" }
            });
        }
    }
}
