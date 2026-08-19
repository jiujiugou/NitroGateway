using Dapper;
using Microsoft.Data.Sqlite;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// app_meta 键值表（M006）读写接口。存储运行期状态（如 MQTT 转发开关），值均为字符串。
/// 只增不删：后续需要键值型运行时状态时复用本接口，避免每处手写 SQL。
/// </summary>
public interface IAppMetaStore
{
    /// <summary>读取指定 key 的 value；key 不存在返回 null</summary>
    /// <param name="key">键名</param>
    /// <param name="ct">取消令牌</param>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>写入/覆盖指定 key 的 value（UPSERT 语义，更新 updated_at）</summary>
    /// <param name="key">键名</param>
    /// <param name="value">值</param>
    /// <param name="ct">取消令牌</param>
    Task SetAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>
/// app_meta 键值表 SQLite 实现（M006）。每次操作使用独立连接（ADR-001 P1-4：共享 Singleton
/// 裸连接跨线程并发不安全，与 SqliteForwardBuffer 同模式）。表不存在时视为缺省（Get 返回 null）。
/// </summary>
public sealed class SqliteAppMetaStore : IAppMetaStore
{
    private readonly string _connectionString;

    /// <param name="connectionString">SQLite 连接串（与其余 Persistence 仓储同源）</param>
    public SqliteAppMetaStore(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        SqlitePragmas.Apply(conn);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT value FROM app_meta WHERE key = @key",
                new { key }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        SqlitePragmas.Apply(conn);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app_meta (key, value, updated_at)
            VALUES (@key, @value, @ts)
            ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @ts
            """,
            new { key, value, ts = DateTime.UtcNow.ToString("O") },
            cancellationToken: ct));
    }
}
