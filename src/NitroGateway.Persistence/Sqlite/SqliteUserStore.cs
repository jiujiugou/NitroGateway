using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NitroGateway.Security.Auth;
using NitroGateway.Shared;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 用户存储 SQLite 实现（ADR-066：用户 DB 化）。Dapper 独立连接（与 MeasurementStore 同模式）：
/// 每操作打开新连接并应用库级 PRAGMA，避免跨线程共享连接；单例注册。
/// <para><b>失败语义：</b>用户是登录/授权的权威身份源，存储异常直接上抛（由异常中间件转 500），
/// 不像审计（best-effort 附加能力）那样吞错——DB 故障≠凭据错误，不能伪装成 401。
/// 仅「用户名唯一冲突」这类可预期业务失败返回 <see cref="OperationResult{T}.Failure"/>。</para>
/// </summary>
public sealed class SqliteUserStore : IUserStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteUserStore> _logger;

    /// <summary>以连接串构造；连接按操作创建，不持有长连接。</summary>
    public SqliteUserStore(string connectionString, ILogger<SqliteUserStore> logger)
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

    /// <summary>查询投影：列名经 SQL 别名对齐 UserAccount 属性（Dapper 按名映射）；时间列按字符串读取后解析</summary>
    private const string SelectColumns =
        "id AS Id, username AS Username, password_hash AS PasswordHash, role AS Role, " +
        "is_enabled AS IsEnabled, created_at AS CreatedAt, updated_at AS UpdatedAt, last_login_at AS LastLoginAt";

    /// <inheritdoc />
    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM users WHERE username = @username",
                new { username },
                cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<UserAccount?> FindByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM users WHERE id = @id",
                new { id },
                cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    /// <remarks>用户量级为个位数，全量返回即可；按用户名排序便于管理页稳定展示。</remarks>
    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM users ORDER BY username",
                cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    /// <remarks>用户名唯一冲突（SQLITE_CONSTRAINT=19）映射为 Validation 失败，其余异常上抛。</remarks>
    public async Task<OperationResult<UserAccount>> CreateAsync(
        string username, string passwordHash, string role, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        try
        {
            await using var conn = await OpenConnectionAsync(ct);
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO users (username, password_hash, role, is_enabled, created_at, updated_at, last_login_at)
                VALUES (@username, @passwordHash, @role, 1, @now, @now, NULL)
                RETURNING id;
                """,
                new { username, passwordHash, role, now = now.ToString("O") },
                cancellationToken: ct));

            return OperationResult<UserAccount>.Success(new UserAccount
            {
                Id = id,
                Username = username,
                PasswordHash = passwordHash,
                Role = role,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT：username 唯一
        {
            _logger.LogWarning("新增用户失败: 用户名 {User} 已存在", username);
            return OperationResult<UserAccount>.Failure(
                OperationalError.Validation($"用户名 {username} 已存在"));
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRoleAsync(int id, string role, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET role = @role, updated_at = @now WHERE id = @id",
            new { id, role, now = DateTime.UtcNow.ToString("O") },
            cancellationToken: ct));
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateEnabledAsync(int id, bool isEnabled, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_enabled = @enabled, updated_at = @now WHERE id = @id",
            new { id, enabled = isEnabled, now = DateTime.UtcNow.ToString("O") },
            cancellationToken: ct));
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePasswordHashAsync(int id, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET password_hash = @hash, updated_at = @now WHERE id = @id",
            new { id, hash = passwordHash, now = DateTime.UtcNow.ToString("O") },
            cancellationToken: ct));
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>仅刷新 last_login_at，不动 updated_at（登录不属于用户管理侧变更）。</remarks>
    public async Task<bool> UpdateLastLoginAsync(int id, DateTime lastLoginAt, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET last_login_at = @lastLogin WHERE id = @id",
            new { id, lastLogin = lastLoginAt.ToString("O") },
            cancellationToken: ct));
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>空表才灌种子，非空返回 0（配置仅引导，不覆盖运行时账号）；单事务批量插入保证半种子不会发生。</remarks>
    public async Task<int> SeedIfEmptyAsync(IReadOnlyList<UserConfig> configUsers, CancellationToken ct = default)
    {
        if (configUsers.Count == 0)
            return 0;

        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM users",
            transaction: tx,
            cancellationToken: ct));
        if (existing > 0)
            return 0;

        var now = DateTime.UtcNow.ToString("O");
        var rows = configUsers.Select(u => new
        {
            username = u.Username,
            passwordHash = u.Password,
            role = u.Role,
            now
        });
        var inserted = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO users (username, password_hash, role, is_enabled, created_at, updated_at, last_login_at)
            VALUES (@username, @passwordHash, @role, 1, @now, @now, NULL)
            """,
            rows,
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        _logger.LogInformation("首启种子完成: 灌入配置用户 {Count} 个", inserted);
        return inserted;
    }

    /// <summary>Dapper 行模型（时间列为 TEXT/NULL 混合，先按字符串读取再解析）</summary>
    private sealed class UserRow
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = "";
        public bool IsEnabled { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? LastLoginAt { get; set; }
    }

    /// <summary>行 → 领域模型；O 格式字符串解析为 UTC DateTime</summary>
    private static UserAccount Map(UserRow r) => new()
    {
        Id = r.Id,
        Username = r.Username,
        PasswordHash = r.PasswordHash,
        Role = r.Role,
        IsEnabled = r.IsEnabled,
        CreatedAt = ParseUtc(r.CreatedAt),
        UpdatedAt = ParseUtc(r.UpdatedAt),
        LastLoginAt = r.LastLoginAt is null ? null : ParseUtc(r.LastLoginAt)
    };

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
