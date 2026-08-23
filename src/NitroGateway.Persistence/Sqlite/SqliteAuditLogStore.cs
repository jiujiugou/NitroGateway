using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NitroGateway.Security.Audit;
using NitroGateway.Shared;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 操作审计日志 SQLite 持久化（ADR-065 A3）。Dapper 独立连接（与 MeasurementStore 同模式）：
/// 每操作打开新连接并应用库级 PRAGMA，避免跨线程共享连接。
/// <para><b>写入契约：</b><see cref="WriteAsync"/> best-effort，任何异常（含表缺失、磁盘满）只记日志不抛出
/// ——审计是附带能力，落库失败绝不能拖垮写值/登录主流程。查询按 O 格式字符串倒序（M005 同约定）。</para>
/// </summary>
public sealed class SqliteAuditLogStore : IAuditLogStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteAuditLogStore> _logger;

    /// <summary>构造审计存储。连接串从 <c>Persistence:ConnectionString</c> 读取（DI 注入）。</summary>
    public SqliteAuditLogStore(string connectionString, ILogger<SqliteAuditLogStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>打开独立连接并应用库级 PRAGMA（WAL/busy_timeout，ADR-001 P1-4）</summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        SqlitePragmas.Apply(conn);
        return conn;
    }

    /// <inheritdoc />
    /// <remarks>请求中止（ct 取消）时放弃写入；其余异常仅记 Warning 日志，不阻断业务请求。</remarks>
    public async Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO audit_logs (id, user, role, method, path, status_code, elapsed_ms, ip, created_at)
                VALUES (@id, @user, @role, @method, @path, @status, @elapsed, @ip, @created)
                """,
                new
                {
                    id = entry.Id.ToString(),
                    user = entry.User,
                    role = entry.Role,
                    method = entry.Method,
                    path = entry.Path,
                    status = entry.StatusCode,
                    elapsed = entry.ElapsedMs,
                    ip = entry.Ip,
                    created = entry.CreatedAt.ToString("O")
                },
                cancellationToken: ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 请求已中止：审计随之放弃，不算失败
        }
        catch (Exception ex)
        {
            // best-effort：审计落库失败仅记日志，不阻断写值/登录等主流程（ADR-065 A3）
            _logger.LogWarning("审计落库失败: {Error}", ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>过滤/分页在 SQL 层完成；created_at 为 O 格式字符串，倒序即时间倒序。</remarks>
    public async Task<OperationResult<AuditLogQueryResult>> QueryAsync(
        AuditLogQuery query, CancellationToken ct = default)
    {
        try
        {
            var safePage = Math.Max(1, query.Page);
            var safePageSize = Math.Clamp(query.PageSize, 1, 200);

            var where = new System.Text.StringBuilder(" WHERE 1=1");
            var ps = new DynamicParameters();
            if (query.From is { } from)
            {
                where.Append(" AND created_at >= @from");
                ps.Add("from", from.ToUniversalTime().ToString("O"));
            }
            if (query.To is { } to)
            {
                where.Append(" AND created_at <= @to");
                ps.Add("to", to.ToUniversalTime().ToString("O"));
            }
            if (!string.IsNullOrWhiteSpace(query.User))
            {
                where.Append(" AND user = @user");
                ps.Add("user", query.User!.Trim());
            }
            if (!string.IsNullOrWhiteSpace(query.Method))
            {
                where.Append(" AND method = @method");
                ps.Add("method", query.Method!.Trim().ToUpperInvariant());
            }
            if (!string.IsNullOrWhiteSpace(query.PathContains))
            {
                where.Append(" AND path LIKE @path");
                ps.Add("path", "%" + query.PathContains!.Trim() + "%");
            }
            if (query.StatusCode is { } code)
            {
                where.Append(" AND status_code = @status");
                ps.Add("status", code);
            }

            await using var conn = await OpenConnectionAsync(ct);

            var total = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(*) FROM audit_logs" + where, ps, cancellationToken: ct));

            ps.Add("limit", safePageSize);
            ps.Add("offset", (safePage - 1) * safePageSize);

            var rows = await conn.QueryAsync<AuditRow>(
                new CommandDefinition(
                    """
                    SELECT id, user, role, method, path,
                           status_code AS StatusCode, elapsed_ms AS ElapsedMs, ip,
                           created_at AS CreatedAt
                    FROM audit_logs
                    """ + where + " ORDER BY created_at DESC LIMIT @limit OFFSET @offset",
                    ps,
                    cancellationToken: ct));

            var items = rows.Select(r => new AuditLogEntry
            {
                Id = Guid.Parse(r.Id),
                User = r.User,
                Role = r.Role,
                Method = r.Method,
                Path = r.Path,
                StatusCode = r.StatusCode,
                ElapsedMs = r.ElapsedMs,
                Ip = r.Ip,
                CreatedAt = DateTime.Parse(r.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind)
            }).ToList();

            return new AuditLogQueryResult { Items = items, Total = total };
        }
        catch (Exception ex)
        {
            _logger.LogError("审计查询失败: {Error}", ex.Message);
            return SqliteErrorClassifier.Classify(ex, "审计查询失败");
        }
    }

    /// <summary>查询行投影（列名经 SQL 别名对齐属性，Dapper 按名映射）</summary>
    private sealed class AuditRow
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
}
