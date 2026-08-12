using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 站点目录实现（Dapper，ADR-035 第 1 步）。
/// 与 <see cref="SqliteMeasurementStore"/> 同模式：每个操作独立创建连接并应用统一 PRAGMA，
/// 单例注册；site 列表 = measurements ∪ alarms 的 site_id 去重（空串排除）。
/// </summary>
public sealed class SqliteSiteCatalog : ISiteCatalog
{
    private readonly string _connectionString;

    /// <summary>以连接串构造；连接按操作创建，不持有长连接。</summary>
    public SqliteSiteCatalog(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<string>>> GetSitesAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            // 站点来源两表合并去重；空串（未标注站点/旧数据）不列入——Web 端"全部站点"天然覆盖
            var rows = await conn.QueryAsync<string>(
                @"SELECT DISTINCT site_id FROM measurements WHERE site_id <> ''
                  UNION
                  SELECT DISTINCT site_id FROM alarms WHERE site_id <> ''
                  ORDER BY site_id",
                ct);

            return OperationResult<IReadOnlyList<string>>.Success(rows.ToList());
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "站点目录查询失败");
        }
    }
}

