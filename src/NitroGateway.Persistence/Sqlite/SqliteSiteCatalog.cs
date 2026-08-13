using Dapper;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 站点目录实现（Dapper，ADR-035 第 1 步 Web 维度 + ADR-036 注册表）。
/// 与 <see cref="SqliteMeasurementStore"/> 同模式：每个操作独立创建连接并应用统一 PRAGMA，单例注册；
/// site 列表 = sites 注册表 ∪ measurements ∪ alarms 的 site_id 去重（空串排除）。
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

            // 站点来源 = sites 注册表 ∪ 两表历史数据去重；空串（未标注站点/旧数据）不列入——
            // 兼容既有部署（站点不因升级消失），新站点由 Ingest 首见注册进 sites（ADR-036）
            var rows = await conn.QueryAsync<string>(
                @"SELECT site_id FROM sites WHERE site_id <> ''
                  UNION
                  SELECT DISTINCT site_id FROM measurements WHERE site_id <> ''
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

    /// <inheritdoc />
    public async Task<OperationResult> RegisterSiteAsync(string siteId, string? sourceClientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            return OperationResult.Success();

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var now = DateTime.UtcNow;

            // ADR-036 upsert：首见插入（保留 source_client_id），后续只更新 last_seen；
            // site_id 唯一约束保证同一站点只有一行（多来源由 last_seen_client_id 变化暴露）
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO sites (site_id, display_name, source_client_id, last_seen_client_id, first_seen_at, last_seen_at)
                  VALUES (@site, '', @client, @client, @now, @now)
                  ON CONFLICT(site_id) DO UPDATE SET
                      last_seen_client_id = excluded.last_seen_client_id,
                      last_seen_at = excluded.last_seen_at",
                new { site = siteId, client = sourceClientId, now },
                cancellationToken: ct));

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "站点注册失败");
        }
    }
    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<SiteInfo>>> GetSiteInfosAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            // ADR-036 中心站点管理：全量站点 = sites 注册表 ∪ (measurements ∪ alarms 的 site_id，排除空串)。
            // 未注册（仅历史数据）站点一并返回：display_name=''、无来源指纹、无时间，供前端改名/建档；
            // 冲突 = source_client_id 与 last_seen_client_id 均非空且不一致（同一 siteId 被不同机器上报）。
            var rows = await conn.QueryAsync<SiteRow>(
                new CommandDefinition(
                    @"SELECT s.site_id AS SiteId,
                             s.display_name AS DisplayName,
                             s.source_client_id AS SourceClientId,
                             s.last_seen_client_id AS LastSeenClientId,
                             s.first_seen_at AS FirstSeenAt,
                             s.last_seen_at AS LastSeenAt
                      FROM sites s
                      UNION
                      SELECT h.site_id AS SiteId, '' AS DisplayName, NULL, NULL, NULL, NULL
                      FROM (SELECT DISTINCT site_id FROM measurements WHERE site_id <> ''
                            UNION
                            SELECT DISTINCT site_id FROM alarms WHERE site_id <> '') h
                      LEFT JOIN sites s2 ON s2.site_id = h.site_id
                      WHERE s2.site_id IS NULL
                      ORDER BY SiteId",
                    cancellationToken: ct));

            var infos = rows.Select(r => new SiteInfo
            {
                SiteId = r.SiteId,
                DisplayName = r.DisplayName,
                SourceClientId = r.SourceClientId,
                LastSeenClientId = r.LastSeenClientId,
                FirstSeenAt = ParseUtc(r.FirstSeenAt),
                LastSeenAt = ParseUtc(r.LastSeenAt),
                HasConflict = r.SourceClientId is not null
                    && r.LastSeenClientId is not null
                    && r.SourceClientId != r.LastSeenClientId
            }).ToList();

            return OperationResult<IReadOnlyList<SiteInfo>>.Success(infos);
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "站点详情查询失败");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> RenameSiteAsync(string siteId, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            return OperationResult.Failure(OperationalError.Validation("siteId 不能为空"));

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var now = DateTime.UtcNow;

            // ADR-036 upsert：未注册（仅历史数据）站点一并建档；已注册仅更新显示名，保留来源指纹与首见时间。
            // 空显示名 = 清除绑定（回到"未命名"），符合 display_name 默认空串的模型。
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO sites (site_id, display_name, source_client_id, last_seen_client_id, first_seen_at, last_seen_at)
                  VALUES (@site, @name, NULL, NULL, @now, @now)
                  ON CONFLICT(site_id) DO UPDATE SET display_name = excluded.display_name",
                new { site = siteId, name = displayName, now },
                cancellationToken: ct));

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "站点改名失败");
        }
    }

    /// <summary>SQLite 时间字符串 → UTC DateTime；NULL 保留 null。</summary>
    private static DateTime? ParseUtc(string? value) =>
        value is null ? null
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    /// <summary>GetSiteInfosAsync 的 Dapper 行模型（时间列为 TEXT/NULL 混合，先按字符串读取）。</summary>
    private sealed class SiteRow
    {
        public string SiteId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? SourceClientId { get; set; }
        public string? LastSeenClientId { get; set; }
        public string? FirstSeenAt { get; set; }
        public string? LastSeenAt { get; set; }
    }}
