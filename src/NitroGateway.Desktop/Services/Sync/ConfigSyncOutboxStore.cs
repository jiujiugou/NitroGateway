using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;

namespace NitroGateway.Desktop.Services.Sync;

/// <summary>待上报变更类型（ADR-033 阶段 4：现场改动先入 outbox，联网后按序上报中心）</summary>
public enum ConfigSyncOutboxKind
{
    /// <summary>设备 upsert（上传时取设备当前全量状态）</summary>
    Device,

    /// <summary>设备删除（tombstone 上报）</summary>
    DeviceDelete,

    /// <summary>点位 upsert（上传时取点位当前状态）</summary>
    Point,

    /// <summary>点位删除（tombstone 上报）</summary>
    PointDelete
}

/// <summary>outbox 行（待上报变更索引；负载在上报时按当前本地状态实时构建）</summary>
public sealed record ConfigSyncOutboxRow
{
    /// <summary>变更类型</summary>
    public required ConfigSyncOutboxKind Kind { get; init; }

    /// <summary>所属设备 ID</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>点位 ID（仅点位类型变更）</summary>
    public Guid? PointId { get; init; }
}

/// <summary>
/// 配置同步 outbox 存储（ADR-033 阶段 4）。
/// 现场 UI 每次设备/点位增删改后写入一行；同步服务上报成功后清除。
/// 设备与点位各用固定主键（设备键含设备 ID、点位键含设备+点位 ID），
/// 因此「删除后又重建」会原地替换行类型，不会同时残留 upsert 与 tombstone。
/// </summary>
public interface IConfigSyncOutboxStore
{
    /// <summary>记录设备 upsert（重建时覆盖既有 device-delete 行）</summary>
    Task<OperationResult> RecordDeviceAsync(Device device, CancellationToken ct = default);

    /// <summary>记录设备删除（覆盖既有 device 行）</summary>
    Task<OperationResult> RecordDeviceDeleteAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>记录点位 upsert（重建时覆盖既有 point-delete 行）</summary>
    Task<OperationResult> RecordPointAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default);

    /// <summary>记录点位删除（覆盖既有 point 行）</summary>
    Task<OperationResult> RecordPointDeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default);

    /// <summary>读取全部待上报变更</summary>
    Task<OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>清除单条变更（上报成功/被中心裁决后）</summary>
    Task<OperationResult> ClearAsync(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId = null, CancellationToken ct = default);

    /// <summary>清除某设备全部变更（含点位行；设备级裁决后使用）</summary>
    Task<OperationResult> ClearForDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>清空全部（手动导入以中心重置本地后，本地与中心一致无待上报）</summary>
    Task<OperationResult> ClearAllAsync(CancellationToken ct = default);
}

/// <summary>SQLite outbox 实现（Dapper，每操作独立连接；桌面库与中心库同 schema，仅桌面写）</summary>
public sealed class ConfigSyncOutboxStore : IConfigSyncOutboxStore
{
    private readonly string _connectionString;

    public ConfigSyncOutboxStore(string connectionString) => _connectionString = connectionString;

    /// <inheritdoc />
    public Task<OperationResult> RecordDeviceAsync(Device device, CancellationToken ct = default)
        => UpsertAsync(ConfigSyncOutboxKind.Device, device.Id, null, ct);

    /// <inheritdoc />
    public Task<OperationResult> RecordDeviceDeleteAsync(Guid deviceId, CancellationToken ct = default)
        => UpsertAsync(ConfigSyncOutboxKind.DeviceDelete, deviceId, null, ct);

    /// <inheritdoc />
    public Task<OperationResult> RecordPointAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => UpsertAsync(ConfigSyncOutboxKind.Point, deviceId, point.Id, ct);

    /// <inheritdoc />
    public Task<OperationResult> RecordPointDeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => UpsertAsync(ConfigSyncOutboxKind.PointDelete, deviceId, pointId, ct);

    /// <inheritdoc />
    public async Task<OperationResult> ClearAsync(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId = null, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM config_sync_outbox WHERE id = @id",
                new { id = Key(kind, deviceId, pointId) }, cancellationToken: ct));
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"配置同步 outbox 清除失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> ClearForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM config_sync_outbox WHERE device_id = @did",
                new { did = deviceId.ToString() }, cancellationToken: ct));
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"配置同步 outbox 清除失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult> ClearAllAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM config_sync_outbox", cancellationToken: ct));
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"配置同步 outbox 清空失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>> GetPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            var rows = await conn.QueryAsync<OutboxRow>(new CommandDefinition(
                "SELECT entity_type, device_id, point_id FROM config_sync_outbox ORDER BY updated_at", cancellationToken: ct));
            return rows.Select(r => new ConfigSyncOutboxRow
            {
                Kind = ParseKind(r.entity_type),
                DeviceId = Guid.Parse(r.device_id),
                PointId = r.point_id is null ? null : Guid.Parse(r.point_id)
            }).ToList();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"配置同步 outbox 读取失败: {ex.Message}");
        }
    }

    private async Task<OperationResult> UpsertAsync(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO config_sync_outbox (id, entity_type, device_id, point_id, updated_at)
                VALUES (@id, @type, @did, @pid, @ts)
                ON CONFLICT(id) DO UPDATE SET entity_type = excluded.entity_type, updated_at = excluded.updated_at
                """,
                new
                {
                    id = Key(kind, deviceId, pointId),
                    type = KindName(kind),
                    did = deviceId.ToString(),
                    pid = pointId?.ToString(),
                    ts = DateTime.UtcNow.ToString("O")
                },
                cancellationToken: ct));
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationalError.General($"配置同步 outbox 写入失败: {ex.Message}");
        }
    }

    /// <summary>设备行共用设备键、点位行共用设备+点位键：删除后重建原地替换行类型</summary>
    private static string Key(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId)
        => kind is ConfigSyncOutboxKind.Point or ConfigSyncOutboxKind.PointDelete
            ? $"p|{deviceId}|{pointId}"
            : $"d|{deviceId}";

    private static string KindName(ConfigSyncOutboxKind kind) => kind switch
    {
        ConfigSyncOutboxKind.Device => "device",
        ConfigSyncOutboxKind.DeviceDelete => "device-delete",
        ConfigSyncOutboxKind.Point => "point",
        _ => "point-delete"
    };

    private static ConfigSyncOutboxKind ParseKind(string value) => value switch
    {
        "device" => ConfigSyncOutboxKind.Device,
        "device-delete" => ConfigSyncOutboxKind.DeviceDelete,
        "point" => ConfigSyncOutboxKind.Point,
        _ => ConfigSyncOutboxKind.PointDelete
    };

    private sealed class OutboxRow
    {
        public string entity_type { get; set; } = "";
        public string device_id { get; set; } = "";
        public string? point_id { get; set; }
    }
}
