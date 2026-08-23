using NitroGateway.Shared;

namespace NitroGateway.Security.Audit;

/// <summary>
/// 操作审计存储接口（ADR-065 A3）。实现放 Persistence（SQLite），Webapi 查询页经控制器读取。
/// <para><b>写入契约：</b><see cref="WriteAsync"/> 必须 best-effort——审计落库失败绝不能阻断业务请求，
/// 实现内部捕获异常仅记日志（由 AuditMiddleware 在请求热路径调用）。</para>
/// </summary>
public interface IAuditLogStore
{
    /// <summary>
    /// 写入一条审计记录（best-effort，实现不得抛出；失败仅记日志）。
    /// 审计是附带能力，落库失败不应让写值/登录等主流程失败。
    /// </summary>
    /// <param name="entry">审计记录</param>
    /// <param name="ct">取消令牌（请求中止时停止等待）</param>
    Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>按过滤条件分页查询审计记录（时间倒序）；失败返回 OperationResult 失败，由控制器转 400。</summary>
    Task<OperationResult<AuditLogQueryResult>> QueryAsync(AuditLogQuery query, CancellationToken ct = default);
}

/// <summary>审计查询过滤条件（ADR-065 A3：时间/操作者/动作/结果过滤）</summary>
public sealed class AuditLogQuery
{
    /// <summary>起始时间（UTC，含）</summary>
    public DateTime? From { get; init; }

    /// <summary>结束时间（UTC，含）</summary>
    public DateTime? To { get; init; }

    /// <summary>操作者精确匹配（空/空串不过滤）</summary>
    public string? User { get; init; }

    /// <summary>HTTP 方法精确匹配（POST/PUT/DELETE/PATCH；空不过滤）</summary>
    public string? Method { get; init; }

    /// <summary>路径包含匹配（区分大小写；空不过滤）</summary>
    public string? PathContains { get; init; }

    /// <summary>状态码精确匹配（如 400/500；null 不过滤）</summary>
    public int? StatusCode { get; init; }

    /// <summary>页码（1 起）</summary>
    public int Page { get; init; } = 1;

    /// <summary>每页条数（夹紧 1..200）</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>审计查询结果：分页条目 + 命中总数（供前端分页）</summary>
public sealed class AuditLogQueryResult
{
    /// <summary>当前页条目（时间倒序）</summary>
    public IReadOnlyList<AuditLogEntry> Items { get; init; } = Array.Empty<AuditLogEntry>();

    /// <summary>满足过滤条件的总条数（不含分页）</summary>
    public int Total { get; init; }
}
