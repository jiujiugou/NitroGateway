namespace NitroGateway.Security.Audit;

public sealed class AuditLogEntry
{
    /// <summary>审计记录 ID</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>操作者用户名（JWT ClaimTypes.Name；未认证请求为 anonymous）</summary>
    public string User { get; init; } = "";

    /// <summary>操作者角色（ClaimTypes.Role；缺失为 "-"）</summary>
    public string Role { get; init; } = "";

    /// <summary>HTTP 方法（POST/PUT/DELETE/PATCH）</summary>
    public string Method { get; init; } = "";

    /// <summary>请求路径（如 /api/devices/{id}/points/{id}/write）</summary>
    public string Path { get; init; } = "";

    /// <summary>响应状态码（真实状态码，异常中间件在内层已把异常转 500 后此处取到）</summary>
    public int StatusCode { get; init; }

    /// <summary>请求耗时（毫秒）</summary>
    public int ElapsedMs { get; init; }

    /// <summary>客户端 IP</summary>
    public string Ip { get; init; } = "";

    /// <summary>记录时间（UTC）</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
