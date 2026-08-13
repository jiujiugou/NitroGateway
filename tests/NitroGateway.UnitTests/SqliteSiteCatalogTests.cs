using Microsoft.Data.Sqlite;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteSiteCatalog 测试（ADR-035 第 1 步 Web 维度）：
/// measurements ∪ alarms 的 site_id 去重、空串排除、排序与异常归类。
/// </summary>
public class SqliteSiteCatalogTests
{
    /// <summary>临时文件库：按 M009 后的结构建 measurements + alarms 表（含 site_id 列），释放时删除文件。</summary>
    private sealed class TempSiteDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempSiteDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-site-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE sites (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    site_id TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL DEFAULT '',
                    source_client_id TEXT NULL,
                    last_seen_client_id TEXT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL
                );
                CREATE TABLE measurements (
                    id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL,
                    point_id TEXT NOT NULL,
                    point_name TEXT NOT NULL,
                    raw_value TEXT NULL,
                    value REAL NULL,
                    data_type TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    quality TEXT NOT NULL,
                    error_msg TEXT NULL,
                    site_id TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE alarms (
                    id TEXT PRIMARY KEY,
                    rule_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    point_id TEXT NOT NULL,
                    trigger_value REAL NOT NULL,
                    threshold REAL NOT NULL,
                    severity TEXT NOT NULL,
                    message TEXT NOT NULL,
                    state TEXT NOT NULL,
                    first_exceeded_at TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    acknowledged_at TEXT NULL,
                    resolved_at TEXT NULL,
                    site_id TEXT NOT NULL DEFAULT ''
                );
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private static void Insert(string connString, string table, string siteId)
    {
        using var conn = new SqliteConnection(connString);
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = table == "measurements"
            ? $"""
               INSERT INTO measurements (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg, site_id)
               VALUES (@id, 'd1', 'p1', 'n', NULL, 1, 'Int16', '2026-08-12T00:00:00.0000000Z', 'Good', NULL, @site)
               """
            : $"""
               INSERT INTO alarms (id, rule_id, device_id, point_id, trigger_value, threshold, severity, message, state, first_exceeded_at, occurred_at, acknowledged_at, resolved_at, site_id)
               VALUES (@id, 'r1', 'd1', 'p1', 1, 1, 'Warning', 'm', 'Active', '2026-08-12T00:00:00.0000000Z', '2026-08-12T00:00:00.0000000Z', NULL, NULL, @site)
               """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@site", siteId);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetSites_UnionsMeasurementsAndAlarms_DeduplicatesAndSorts()
    {
        using var db = new TempSiteDb();
        Insert(db.ConnectionString, "measurements", "site-b");
        Insert(db.ConnectionString, "measurements", "site-a");
        Insert(db.ConnectionString, "measurements", "site-b");   // 重复：仅出现一次
        Insert(db.ConnectionString, "alarms", "site-a");         // 与 measurements 重复
        Insert(db.ConnectionString, "alarms", "site-c");
        Insert(db.ConnectionString, "measurements", "");         // 未标注：不列入

        var catalog = new SqliteSiteCatalog(db.ConnectionString);
        var r = await catalog.GetSitesAsync();

        Assert.True(r.IsSuccess);
        Assert.Equal(new[] { "site-a", "site-b", "site-c" }, r.Value);
    }

    [Fact]
    public async Task GetSites_EmptyTables_ReturnsEmpty()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        var r = await catalog.GetSitesAsync();

        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value);
    }

    [Fact]
    public async Task GetSites_MissingTable_ReturnsClassifiedFailure()
    {
        using var db = new TempSiteDb();
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "DROP TABLE alarms; DROP TABLE measurements; DROP TABLE sites;";
            command.ExecuteNonQuery();
        }

        var catalog = new SqliteSiteCatalog(db.ConnectionString);
        var r = await catalog.GetSitesAsync();

        Assert.True(r.IsFailure);
    }

    [Fact]
    public async Task RegisterSite_inserts_once_and_keeps_source_fingerprint()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        var first = await catalog.RegisterSiteAsync("site-x", "NitroGateway-PC1-aaa", CancellationToken.None);
        var second = await catalog.RegisterSiteAsync("site-x", "NitroGateway-PC2-bbb", CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);

        // site_id 唯一索引兜底：同一站点只有一行，首见来源指纹保留，last_seen 更新
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT source_client_id, last_seen_client_id FROM sites WHERE site_id = 'site-x'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("NitroGateway-PC1-aaa", reader.GetString(0));
            Assert.Equal("NitroGateway-PC2-bbb", reader.GetString(1));
            Assert.False(reader.Read());
        }

        var r = await catalog.GetSitesAsync();
        Assert.True(r.IsSuccess);
        Assert.Equal(new[] { "site-x" }, r.Value);
    }

    [Fact]
    public async Task RegisterSite_empty_site_id_is_noop()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        var r = await catalog.RegisterSiteAsync("", null, CancellationToken.None);

        Assert.True(r.IsSuccess);
        var sites = await catalog.GetSitesAsync();
        Assert.True(sites.IsSuccess);
        Assert.Empty(sites.Value);
    }

    [Fact]
    public async Task GetSiteInfos_RegisteredAndHistorical_ReturnsConflictFlag()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        // 同一站点被两台机器上报 → 冲突；另有一台只上报未注册站点
        await catalog.RegisterSiteAsync("site-x", "PC-1-aaa", CancellationToken.None);
        await catalog.RegisterSiteAsync("site-x", "PC-2-bbb", CancellationToken.None);
        Insert(db.ConnectionString, "measurements", "site-y");

        var r = await catalog.GetSiteInfosAsync();

        Assert.True(r.IsSuccess);
        var siteX = r.Value!.Single(s => s.SiteId == "site-x");
        Assert.Equal("PC-1-aaa", siteX.SourceClientId);
        Assert.Equal("PC-2-bbb", siteX.LastSeenClientId);
        Assert.True(siteX.HasConflict);
        Assert.NotNull(siteX.FirstSeenAt);
        Assert.NotNull(siteX.LastSeenAt);

        // 未注册（仅历史数据）站点：display_name 空、无指纹、无时间
        var siteY = r.Value!.Single(s => s.SiteId == "site-y");
        Assert.Equal("", siteY.DisplayName);
        Assert.Null(siteY.SourceClientId);
        Assert.Null(siteY.LastSeenClientId);
        Assert.False(siteY.HasConflict);
        Assert.Null(siteY.FirstSeenAt);

        // 排序：site-x < site-y
        Assert.Equal(new[] { "site-x", "site-y" }, r.Value!.Select(s => s.SiteId).ToArray());
    }

    [Fact]
    public async Task GetSiteInfos_SameClient_NoConflict()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        await catalog.RegisterSiteAsync("site-x", "PC-1-aaa", CancellationToken.None);
        await catalog.RegisterSiteAsync("site-x", "PC-1-aaa", CancellationToken.None);

        var r = await catalog.GetSiteInfosAsync();

        Assert.True(r.IsSuccess);
        var siteX = r.Value!.Single();
        Assert.False(siteX.HasConflict);
    }

    [Fact]
    public async Task RenameSite_CreatesUnregisteredSite_ThenUpdatesName()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);
        Insert(db.ConnectionString, "alarms", "site-z");   // 仅历史数据，未注册

        var created = await catalog.RenameSiteAsync("site-z", "测试站", CancellationToken.None);
        Assert.True(created.IsSuccess);

        // 未注册站点建档：display_name 写入，来源指纹为空
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT display_name, source_client_id FROM sites WHERE site_id = 'site-z'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("测试站", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }

        var renamed = await catalog.RenameSiteAsync("site-z", "新名字", CancellationToken.None);
        Assert.True(renamed.IsSuccess);

        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT display_name, first_seen_at, last_seen_at FROM sites WHERE site_id = 'site-z'";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("新名字", reader.GetString(0));
            Assert.Equal(reader.GetString(1), reader.GetString(2));   // upsert 保留首见时间（新建行时间相等）
        }
    }

    [Fact]
    public async Task RenameSite_EmptySiteId_ReturnsFailure()
    {
        using var db = new TempSiteDb();
        var catalog = new SqliteSiteCatalog(db.ConnectionString);

        var r = await catalog.RenameSiteAsync("", "x", CancellationToken.None);

        Assert.True(r.IsFailure);
    }
}
