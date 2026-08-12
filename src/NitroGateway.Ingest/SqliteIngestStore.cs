using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Measurements;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;

namespace NitroGateway.Ingest;

/// <summary>
/// SQLite 中心入库实现（Dapper，每操作独立连接）。
/// 复用 M001~ 迁移建表（D3：中心库与现场库同 schema）；遥测 INSERT OR IGNORE（D2 记录级幂等），
/// 告警 UPSERT（状态迁移按 alarmId 覆盖）。异常统一归类返回，不抛出。
/// </summary>
public sealed class SqliteIngestStore : IIngestStore
{
    /// <summary>连接串（Data Source=...）</summary>
    private readonly string _connectionString;

    /// <summary>raw_value JSON 序列化选项：与现场 SqliteMeasurementStore 口径一致（camelCase）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>创建入库实现</summary>
    /// <param name="connectionString">SQLite 连接串（复用中心库迁移）</param>
    public SqliteIngestStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等键为 MeasurementRecord.Id（measurements.id 主键）。
    /// INSERT OR IGNORE 返回实际新增行数，去重数 = 记录数 - 新增数（D2）。
    /// 时间戳统一转 UTC O 格式字符串，与现场写入口径一致（字典序即时间序）。
    /// </remarks>
    public async Task<OperationResult<IngestWriteResult>> WriteMeasurementsAsync(
        IReadOnlyList<MeasurementRecord> records, CancellationToken ct = default)
        => await WriteMeasurementsAsync(records, "", ct);

    /// <inheritdoc />
    public async Task<OperationResult<IngestWriteResult>> WriteMeasurementsAsync(
        IReadOnlyList<MeasurementRecord> records, string siteId, CancellationToken ct = default)
    {
        if (records.Count == 0)
            return OperationResult<IngestWriteResult>.Success(new IngestWriteResult(0, 0));

        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var inserted = await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT OR IGNORE INTO measurements
                    (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg, site_id)
                VALUES (@id, @did, @pid, @name, @raw, @val, @type, @ts, @qual, @err, @site)
                """,
                records.Select(r => new
                {
                    // D2 幂等键：记录 ID 直接做主键，重复投递被 OR IGNORE 忽略
                    id = r.Id.ToString("D"),
                    did = r.DeviceId.ToString("D"),
                    pid = r.DevicePointId.ToString("D"),
                    name = r.PointName ?? string.Empty,
                    raw = JsonSerializer.Serialize(r.Value, JsonOptions),
                    val = ToDoubleOrNull(r.Value),
                    type = r.DataType.ToString(),
                    ts = r.Timestamp.ToUniversalTime().ToString("O"),
                    qual = r.Quality.ToString(),
                    err = (object?)DBNull.Value,
                    site = siteId
                }),
                cancellationToken: ct));

            return OperationResult<IngestWriteResult>.Success(new IngestWriteResult(records.Count, inserted));
        }
        catch (Exception ex)
        {
            return OperationResult<IngestWriteResult>.Failure(
                OperationalError.General($"中心遥测入库失败: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等键为 AlarmId（alarms.id 主键）。告警状态迁移（Pending/Active/Acknowledged/Resolved）
    /// 是同一告警的生命周期更新，须 UPSERT 覆盖而非忽略，否则中心状态停留在首次收到值。
    /// first_exceeded_at/acknowledged_at/resolved_at 不在上行契约内，保持 NULL。
    /// </remarks>
    public async Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, CancellationToken ct = default)
        => await UpsertAlarmAsync(alarm, "", ct);

    /// <inheritdoc />
    public async Task<OperationResult> UpsertAlarmAsync(IngestAlarmMessage alarm, string siteId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO alarms (id, rule_id, device_id, point_id, trigger_value, threshold, severity, message, state, occurred_at, site_id)
                VALUES (@id, @ruleId, @deviceId, @pointId, @triggerValue, @threshold, @severity, @message, @state, @occurredAt, @site)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state,
                    trigger_value = excluded.trigger_value,
                    threshold = excluded.threshold,
                    severity = excluded.severity,
                    message = excluded.message,
                    occurred_at = excluded.occurred_at,
                    site_id = excluded.site_id
                """,
                new
                {
                    id = alarm.AlarmId.ToString("D"),
                    ruleId = alarm.RuleId.ToString("D"),
                    deviceId = alarm.DeviceId.ToString("D"),
                    pointId = alarm.PointId.ToString("D"),
                    triggerValue = (object?)alarm.TriggerValue ?? DBNull.Value,
                    threshold = (object?)alarm.Threshold ?? DBNull.Value,
                    severity = alarm.Severity,
                    message = alarm.Message,
                    state = alarm.State,
                    occurredAt = alarm.OccurredAt.ToUniversalTime().ToString("O"),
                    site = siteId
                },
                cancellationToken: ct));

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Failure(OperationalError.General($"中心告警入库失败: {ex.Message}"));
        }
    }

    /// <summary>
    /// 值 → value 列（REAL）：数字直转；字符串按不变文化解析；复合类型（寄存器数组等）存 NULL。
    /// 与现场 SqliteMeasurementStore 的落库口径一致（raw_value 保留完整 JSON，value 仅数值）。
    /// </summary>
    private static double? ToDoubleOrNull(object? value)
    {
        if (value is null) return null;

        if (value is JsonElement element)
        {
            // 来自网络反序列化的值是 JsonElement：按 JSON 数字/字符串取值
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
                return number;
            if (element.ValueKind == JsonValueKind.String
                && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        return value is IConvertible
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture)
            : null;
    }
}
