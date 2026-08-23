using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Security.Audit;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteAuditLogStore 测试（ADR-065 A3 操作审计落库/查询）：
/// 写 + 查询（时间倒序、过滤、分页）、表缺失时写 best-effort 不抛出、
/// 查询失败按 OperationResult 分类返回（不向调用方抛 SQLite 异常）。
/// </summary>
public class SqliteAuditLogStoreTests
{
    /// <summary>临时文件库：按 M014 迁移结构建 audit_logs 表，释放时删除文件。</summary>
    private sealed class TempAuditDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempAuditDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-audit-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE audit_logs (
                    id TEXT PRIMARY KEY,
                    user TEXT NOT NULL,
                    role TEXT NOT NULL,
                    method TEXT NOT NULL,
                    path TEXT NOT NULL,
                    status_code INTEGER NOT NULL,
                    elapsed_ms INTEGER NOT NULL,
                    ip TEXT NOT NULL,
                    created_at TEXT NOT NULL
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

    private readonly TempAuditDb _db = new();

    private SqliteAuditLogStore CreateStore()
        => new(_db.ConnectionString, NullLogger<SqliteAuditLogStore>.Instance);

    private static AuditLogEntry NewEntry(string user, string method, string path, int statusCode = 200, DateTime? createdAt = null) => new()
    {
        User = user,
        Role = "Admin",
        Method = method,
        Path = path,
        StatusCode = statusCode,
        ElapsedMs = 12,
        Ip = "127.0.0.1",
        CreatedAt = createdAt ?? DateTime.UtcNow
    };

    [Fact]
    public async Task WriteAsync_ThenQuery_ReturnsEntryTimeDesc()
    {
        var store = CreateStore();
        var old = NewEntry("admin", "POST", "/api/devices", 200, DateTime.UtcNow.AddMinutes(-5));
        var recent = NewEntry("operator", "PUT", "/api/devices/x", 200, DateTime.UtcNow.AddMinutes(-1));

        await store.WriteAsync(old);
        await store.WriteAsync(recent);

        var result = await store.QueryAsync(new AuditLogQuery { Page = 1, PageSize = 50 });
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Total);
        // 时间倒序：最近一条在前
        Assert.Collection(result.Value.Items,
            e => Assert.Equal(recent.User, e.User),
            e => Assert.Equal(old.User, e.User));
        Assert.Equal("PUT", result.Value.Items[0].Method);
        Assert.Equal("operator", result.Value.Items[0].User);
        Assert.Equal(12, result.Value.Items[0].ElapsedMs);
        Assert.Equal("127.0.0.1", result.Value.Items[0].Ip);
    }

    [Fact]
    public async Task QueryAsync_Filters_UserMethodPathStatus()
    {
        var store = CreateStore();
        await store.WriteAsync(NewEntry("admin", "POST", "/api/devices", 200));
        await store.WriteAsync(NewEntry("admin", "POST", "/api/devices", 400));
        await store.WriteAsync(NewEntry("operator", "PUT", "/api/points/write", 200));

        var byUser = await store.QueryAsync(new AuditLogQuery { User = "operator" });
        Assert.Equal(1, byUser.Value!.Total);

        var byMethod = await store.QueryAsync(new AuditLogQuery { Method = "POST" });
        Assert.Equal(2, byMethod.Value!.Total);

        var byPath = await store.QueryAsync(new AuditLogQuery { PathContains = "points/write" });
        Assert.Equal(1, byPath.Value!.Total);

        var byStatus = await store.QueryAsync(new AuditLogQuery { StatusCode = 400 });
        Assert.Equal(1, byStatus.Value!.Total);
    }

    [Fact]
    public async Task QueryAsync_TimeRange_Filters()
    {
        var store = CreateStore();
        await store.WriteAsync(NewEntry("a", "POST", "/api/x", 200, DateTime.UtcNow.AddHours(-2)));
        await store.WriteAsync(NewEntry("b", "POST", "/api/x", 200, DateTime.UtcNow.AddMinutes(-10)));

        var result = await store.QueryAsync(new AuditLogQuery
        {
            From = DateTime.UtcNow.AddHours(-1),
            To = DateTime.UtcNow.AddMinutes(-1)
        });

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal("b", item.User);
    }

    [Fact]
    public async Task QueryAsync_Pagination_ReturnsPageAndTotal()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
            await store.WriteAsync(NewEntry($"u{i}", "POST", $"/api/devices/{i}", 200));

        var page1 = await store.QueryAsync(new AuditLogQuery { Page = 1, PageSize = 2 });
        Assert.Equal(5, page1.Value!.Total);
        Assert.Equal(2, page1.Value.Items.Count);
        Assert.Equal("u4", page1.Value.Items[0].User); // 倒序：最新（后写）在前

        var page3 = await store.QueryAsync(new AuditLogQuery { Page = 3, PageSize = 2 });
        Assert.Equal(1, page3.Value!.Items.Count);
        Assert.Equal("u0", page3.Value.Items[0].User);
    }

    /// <summary>best-effort 契约：审计表缺失时写不抛出（不阻断写值/登录主流程）</summary>
    [Fact]
    public async Task WriteAsync_TableMissing_DoesNotThrow()
    {
        // 建库时已建表，这里删掉模拟未迁移场景
        using (var conn = new SqliteConnection(_db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE audit_logs";
            cmd.ExecuteNonQuery();
        }

        var store = CreateStore();

        // 表缺失时 WriteAsync 静默失败（仅日志），绝不抛异常
        await store.WriteAsync(NewEntry("admin", "POST", "/api/devices", 200));
    }

    /// <summary>查询失败（表缺失）按 OperationResult 失败分类返回，不抛 SQLite 异常</summary>
    [Fact]
    public async Task QueryAsync_TableMissing_ReturnsFailureNotException()
    {
        using (var conn = new SqliteConnection(_db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE audit_logs";
            cmd.ExecuteNonQuery();
        }

        var store = CreateStore();

        var result = await store.QueryAsync(new AuditLogQuery { Page = 1, PageSize = 50 });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }
}
