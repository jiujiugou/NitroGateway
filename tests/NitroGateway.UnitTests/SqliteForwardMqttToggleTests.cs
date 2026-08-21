using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Storage.Buffer;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// MQTT 转发总开关的 app_meta 持久化实现测试（ADR-059）：
/// 内存缓存（热路径同步读）与持久化（app_meta 键值，重启保持）分离，
/// 缺省/读失败按启用处理，持久化失败不改内存态。
/// </summary>
public sealed class SqliteForwardMqttToggleTests : IDisposable
{
    private sealed class TempMetaDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempMetaDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-meta-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE app_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
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

    private readonly TempMetaDb _db = new();

    public void Dispose() => _db.Dispose();

    private SqliteForwardMqttToggle NewToggle()
        => new(new SqliteAppMetaStore(_db.ConnectionString), NullLogger<SqliteForwardMqttToggle>.Instance);

    /// <summary>app_meta 中已持久化的开关值（null=键不存在）。</summary>
    private string? ReadPersisted()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT value FROM app_meta WHERE key = @key";
        command.Parameters.AddWithValue("@key", SqliteForwardMqttToggle.Key);
        return (string?)command.ExecuteScalar();
    }

    [Fact]
    public void Default_IsEnabled_true_before_initialize()
    {
        Assert.True(NewToggle().IsEnabled);
    }

    [Fact]
    public async Task Initialize_missing_key_treats_as_enabled()
    {
        var toggle = NewToggle();
        var result = await toggle.InitializeAsync();

        Assert.True(result.IsSuccess);
        Assert.True(toggle.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_persists_to_app_meta_and_updates_memory()
    {
        var toggle = NewToggle();
        var result = await toggle.SetEnabledAsync(false);

        Assert.True(result.IsSuccess);
        Assert.False(toggle.IsEnabled);
        Assert.Equal("false", ReadPersisted());
    }

    [Fact]
    public async Task Persisted_value_survives_restart()
    {
        var first = NewToggle();
        await first.SetEnabledAsync(false);

        // 模拟重启：全新实例从 app_meta 加载持久值
        var second = NewToggle();
        var result = await second.InitializeAsync();

        Assert.True(result.IsSuccess);
        Assert.False(second.IsEnabled);
    }

    [Fact]
    public async Task Initialize_loads_persisted_false()
    {
        using var seed = new SqliteConnection(_db.ConnectionString);
        seed.Open();
        using var command = seed.CreateCommand();
        command.CommandText = "INSERT INTO app_meta (key, value, updated_at) VALUES (@key, 'false', @ts)";
        command.Parameters.AddWithValue("@key", SqliteForwardMqttToggle.Key);
        command.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        var toggle = NewToggle();
        await toggle.InitializeAsync();

        Assert.False(toggle.IsEnabled);
    }

    [Fact]
    public async Task Initialize_missing_table_falls_back_to_enabled()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE app_meta";
            drop.ExecuteNonQuery();
        }

        var toggle = NewToggle();
        var result = await toggle.InitializeAsync();

        // 读失败不阻断启动：按启用处理，返回成功
        Assert.True(result.IsSuccess);
        Assert.True(toggle.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_failure_keeps_memory_state_unchanged()
    {
        var toggle = NewToggle();
        Assert.True(toggle.IsEnabled);

        // 表被删后写入失败：内存态保持原值（启用），结果失败
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE app_meta";
            drop.ExecuteNonQuery();
        }

        var result = await toggle.SetEnabledAsync(false);

        Assert.True(result.IsFailure);
        Assert.True(toggle.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_raises_EnabledChanged_only_on_actual_change()
    {
        // ADR-061：SetEnabledAsync 持久化成功且实际值变化才触发事件；
        // 重复设置同一值不触发（避免多余断开/重连）。
        var toggle = NewToggle();
        var raised = new List<bool>();
        toggle.EnabledChanged += b => raised.Add(b);

        await toggle.SetEnabledAsync(false);
        Assert.Equal(new[] { false }, raised);

        await toggle.SetEnabledAsync(false); // 值未变，不触发
        Assert.Single(raised);

        await toggle.SetEnabledAsync(true);
        Assert.Equal(new[] { false, true }, raised);
    }

    [Fact]
    public async Task Initialize_does_not_raise_EnabledChanged()
    {
        // ADR-061：启动加载持久值不触发事件（避免启动时误触发断开/重连）
        var toggle = NewToggle();
        var raised = new List<bool>();
        toggle.EnabledChanged += b => raised.Add(b);

        await toggle.InitializeAsync();
        Assert.Empty(raised);
    }
}
