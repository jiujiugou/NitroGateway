using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Security.Auth;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteUserStore 测试（ADR-066 用户 DB 化）：首启种子、增查改删、用户名唯一、
/// 启停/改角色/改密即时生效（存储层写入后读取即最新）。
/// </summary>
public class SqliteUserStoreTests
{
    /// <summary>临时文件库：按 M015 迁移结构建 users 表，释放时删除文件。</summary>
    private sealed class TempUserDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempUserDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-users-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    role TEXT NOT NULL,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_login_at TEXT NULL
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

    private readonly TempUserDb _db = new();

    private SqliteUserStore CreateStore()
        => new(_db.ConnectionString, NullLogger<SqliteUserStore>.Instance);

    private static UserConfig NewConfig(string username, string role = "Operator")
        => new() { Username = username, Password = "AQAAAAIAAYagAAAAEFAK", Role = role };

    [Fact]
    public async Task SeedIfEmptyAsync_EmptyTable_InsertsConfigUsers()
    {
        var store = CreateStore();
        var config = new[] { NewConfig("admin", "Admin"), NewConfig("operator"), NewConfig("viewer", "Viewer") };

        var inserted = await store.SeedIfEmptyAsync(config);

        Assert.Equal(3, inserted);
        var users = await store.ListAsync();
        Assert.Equal(3, users.Count);
        Assert.Equal("Admin", (await store.FindByUsernameAsync("admin"))!.Role);
        Assert.Equal("Viewer", (await store.FindByUsernameAsync("viewer"))!.Role);
    }

    [Fact]
    public async Task SeedIfEmptyAsync_NonEmptyTable_DoesNotOverwrite()
    {
        var store = CreateStore();
        await store.SeedIfEmptyAsync(new[] { NewConfig("admin", "Admin") });

        // 表已非空：再次种子返回 0，不覆盖（配置仅引导）
        var inserted = await store.SeedIfEmptyAsync(new[] { NewConfig("operator") });

        Assert.Equal(0, inserted);
        var users = await store.ListAsync();
        var user = Assert.Single(users);
        Assert.Equal("admin", user.Username);
    }

    [Fact]
    public async Task CreateAsync_ThenFindByUsername_ReturnsUser()
    {
        var store = CreateStore();

        var result = await store.CreateAsync("admin", "hash", "Admin");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Id > 0);
        Assert.True(result.Value.IsEnabled);
        var found = await store.FindByUsernameAsync("admin");
        Assert.NotNull(found);
        Assert.Equal("hash", found!.PasswordHash);
        Assert.Equal("Admin", found.Role);
    }

    [Fact]
    public async Task CreateAsync_DuplicateUsername_Fails()
    {
        var store = CreateStore();
        await store.CreateAsync("admin", "hash", "Admin");

        var result = await store.CreateAsync("admin", "other", "Operator");

        Assert.True(result.IsFailure);
        Assert.Contains("已存在", result.Error!.Message);
    }

    [Fact]
    public async Task UpdateRoleAsync_ThenFind_ReturnsNewRole()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync("admin", "hash", "Admin")).Value!;

        var ok = await store.UpdateRoleAsync(created.Id, "Viewer");

        Assert.True(ok);
        Assert.Equal("Viewer", (await store.FindByIdAsync(created.Id))!.Role);
    }

    [Fact]
    public async Task UpdateEnabledAsync_Disable_ThenLoginSeesDisabled()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync("admin", "hash", "Admin")).Value!;

        var ok = await store.UpdateEnabledAsync(created.Id, false);

        Assert.True(ok);
        Assert.False((await store.FindByIdAsync(created.Id))!.IsEnabled);
    }

    [Fact]
    public async Task UpdatePasswordHashAsync_ThenFind_ReturnsNewHash()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync("admin", "old-hash", "Admin")).Value!;

        var ok = await store.UpdatePasswordHashAsync(created.Id, "new-hash");

        Assert.True(ok);
        Assert.Equal("new-hash", (await store.FindByIdAsync(created.Id))!.PasswordHash);
    }

    [Fact]
    public async Task UpdateLastLoginAsync_ThenFind_ReturnsTimestamp()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync("admin", "hash", "Admin")).Value!;
        var loginAt = new DateTime(2026, 8, 23, 1, 2, 3, DateTimeKind.Utc);

        var ok = await store.UpdateLastLoginAsync(created.Id, loginAt);

        Assert.True(ok);
        Assert.Equal(loginAt, (await store.FindByIdAsync(created.Id))!.LastLoginAt);
    }

    [Fact]
    public async Task DeleteAsync_ThenFind_ReturnsNull()
    {
        var store = CreateStore();
        var created = (await store.CreateAsync("admin", "hash", "Admin")).Value!;

        var ok = await store.DeleteAsync(created.Id);

        Assert.True(ok);
        Assert.Null(await store.FindByIdAsync(created.Id));
        Assert.False(await store.DeleteAsync(created.Id)); // 已删，再次删除返回 false
    }

    [Fact]
    public async Task UpdateOnMissingUser_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.False(await store.UpdateRoleAsync(999, "Admin"));
        Assert.False(await store.UpdateEnabledAsync(999, false));
        Assert.False(await store.UpdatePasswordHashAsync(999, "hash"));
        Assert.False(await store.UpdateLastLoginAsync(999, DateTime.UtcNow));
    }
}
