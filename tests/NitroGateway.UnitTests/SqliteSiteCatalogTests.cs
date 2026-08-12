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
            command.CommandText = "DROP TABLE alarms; DROP TABLE measurements;";
            command.ExecuteNonQuery();
        }

        var catalog = new SqliteSiteCatalog(db.ConnectionString);
        var r = await catalog.GetSitesAsync();

        Assert.True(r.IsFailure);
    }
}
