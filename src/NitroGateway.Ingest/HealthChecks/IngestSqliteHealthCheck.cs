using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NitroGateway.Ingest.HealthChecks;

/// <summary>中心 SQLite 健康检查：打开连接即视为健康（与 Webapi SqliteHealthCheck 同口径）</summary>
public sealed class IngestSqliteHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public IngestSqliteHealthCheck(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"中心 SQLite 不可用: {ex.Message}");
        }
    }
}
