using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NitroGateway.Webapi.HealthChecks;

/// <summary>SQLite 健康检查：验证数据库可读写</summary>
public sealed class SqliteHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public SqliteHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);

            return HealthCheckResult.Healthy("SQLite 可读写");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQLite 不可用", ex);
        }
    }
}
