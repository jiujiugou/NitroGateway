using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 时序数据存储实现（Dapper）</summary>
public sealed class SqliteMeasurementStore : IMeasurementStore
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SqliteMeasurementStore(string connString) { _connectionString = connString; }

    public async Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.SqliteWrite);
        activity?.SetTag(GatewayActivityTags.TableName, "measurements");
        activity?.SetTag(GatewayActivityTags.SnapshotCount, snapshots.Count);

        if (snapshots.Count == 0) { activity?.SetStatus(ActivityStatusCode.Ok); return OperationResult.Success(); }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        SqlitePragmas.Apply(conn);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(
                @"INSERT INTO measurements (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg)
                  VALUES (@id, @did, @pid, @name, @raw, @val, @type, @ts, @qual, @err)",
                snapshots.Select(s => new
                {
                    id = Guid.NewGuid().ToString(),
                    did = s.DeviceId.ToString(),
                    pid = s.DevicePointId.ToString(),
                    // ADR-002 P1-3：写入真实点位名（修复前写空串，列存在但数据丢失）
                    name = s.PointName ?? string.Empty,
                    raw = Serialize(s.RawValue),
                    val = s.Value is IConvertible ? Convert.ToDouble(s.Value) : (object)DBNull.Value,
                    // ADR-002 P1-3：写入真实数据类型（修复前写空串）
                    type = s.DataType.ToString(),
                    ts = s.Timestamp.ToUniversalTime().ToString("O"),
                    qual = s.Quality.ToString(),
                    err = (object?)s.ErrorMessage ?? DBNull.Value
                }), tx);

            await tx.CommitAsync(ct);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            return SqliteErrorClassifier.Classify(ex, "时序数据写入失败");
        }
    }

    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // ADR-002 P1-1：查询异常统一走 SqliteErrorClassifier，返回 OperationResult 而非向调用方抛异常
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var rows = await conn.QueryAsync(
                @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                  FROM measurements WHERE device_id = @did AND point_id = @pid AND timestamp BETWEEN @from AND @to
                  ORDER BY timestamp ASC",
                new { did = deviceId.ToString(), pid = pointId.ToString(), from = from.ToUniversalTime().ToString("O"), to = to.ToUniversalTime().ToString("O") });

            return rows.Select(r => new PointSnapshot
            {
                DeviceId = Guid.Parse((string)r.device_id),
                DevicePointId = Guid.Parse((string)r.point_id),
                // ADR-002 P1-3：回填点位名与数据类型（修复前查询不读这两列）
                PointName = r.point_name as string,
                RawValue = Deserialize(r.raw_value as string),
                Value = r.value is DBNull ? null : (double)r.value,
                DataType = ParseDataType(r.data_type as string),
                Timestamp = DateTime.Parse((string)r.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Quality = Enum.Parse<QualityCode>((string)r.quality),
                ErrorMessage = r.error_msg as string
            }).ToList();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据查询失败");
        }
    }

    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        // ADR-002 P1-1：查询异常统一走 SqliteErrorClassifier，返回 OperationResult 而非向调用方抛异常
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var rows = await conn.QueryAsync(
                @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                  FROM measurements WHERE device_id = @did AND timestamp BETWEEN @from AND @to
                  ORDER BY timestamp DESC",
                new { did = deviceId.ToString(), from = from.ToUniversalTime().ToString("O"), to = to.ToUniversalTime().ToString("O") });

            return rows.Select(r => new PointSnapshot
            {
                DeviceId = Guid.Parse((string)r.device_id),
                DevicePointId = Guid.Parse((string)r.point_id),
                // ADR-002 P1-3：回填点位名与数据类型（修复前查询不读这两列）
                PointName = r.point_name as string,
                RawValue = Deserialize(r.raw_value as string),
                Value = r.value is DBNull ? null : (double)r.value,
                DataType = ParseDataType(r.data_type as string),
                Timestamp = DateTime.Parse((string)r.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Quality = Enum.Parse<QualityCode>((string)r.quality),
                ErrorMessage = r.error_msg as string
            }).ToList();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据查询失败");
        }
    }

    /// <summary>
    /// ADR-005 P2-2：分页查询，LIMIT/OFFSET 控制单次返回量。
    /// pointId 为 null 时查设备全部点位；limit 夹紧 1..1000，offset 夹紧 ≥0。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit, 1, 1000);
            var safeOffset = Math.Max(0, offset);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var sql = pointId.HasValue
                ? @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND point_id = @pid AND timestamp BETWEEN @from AND @to
                    ORDER BY timestamp ASC LIMIT @limit OFFSET @offset"
                : @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND timestamp BETWEEN @from AND @to
                    ORDER BY timestamp ASC LIMIT @limit OFFSET @offset";

            var rows = await conn.QueryAsync(sql, new
            {
                did = deviceId.ToString(),
                pid = pointId?.ToString(),
                from = from.ToUniversalTime().ToString("O"),
                to = to.ToUniversalTime().ToString("O"),
                limit = safeLimit,
                offset = safeOffset
            });

            return rows.Select(r => new PointSnapshot
            {
                DeviceId = Guid.Parse((string)r.device_id),
                DevicePointId = Guid.Parse((string)r.point_id),
                PointName = r.point_name as string,
                RawValue = Deserialize(r.raw_value as string),
                Value = r.value is DBNull ? null : (double)r.value,
                DataType = ParseDataType(r.data_type as string),
                Timestamp = DateTime.Parse((string)r.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Quality = Enum.Parse<QualityCode>((string)r.quality),
                ErrorMessage = r.error_msg as string
            }).ToList();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据查询失败");
        }
    }

    /// <summary>
    /// ADR-002 P2-4：查询最新快照。
    /// pointId 非 null 取该点位最新一条（ORDER BY timestamp DESC LIMIT 1）；
    /// pointId 为 null 按 point_id 分组取每点最新一条（timestamp 为 "O" 格式 UTC，字典序即时间序）。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var sql = pointId.HasValue
                ? @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND point_id = @pid
                    ORDER BY timestamp DESC LIMIT 1"
                : @"SELECT m.device_id, m.point_id, m.point_name, m.raw_value, m.value, m.data_type, m.timestamp, m.quality, m.error_msg
                    FROM measurements m
                    INNER JOIN (
                        SELECT point_id, MAX(timestamp) AS max_ts
                        FROM measurements
                        WHERE device_id = @did
                        GROUP BY point_id
                    ) latest ON latest.point_id = m.point_id AND latest.max_ts = m.timestamp
                    WHERE m.device_id = @did";

            var rows = await conn.QueryAsync(sql,
                new { did = deviceId.ToString(), pid = pointId?.ToString() });

            return rows.Select(r => new PointSnapshot
            {
                DeviceId = Guid.Parse((string)r.device_id),
                DevicePointId = Guid.Parse((string)r.point_id),
                PointName = r.point_name as string,
                RawValue = Deserialize(r.raw_value as string),
                Value = r.value is DBNull ? null : (double)r.value,
                DataType = ParseDataType(r.data_type as string),
                Timestamp = DateTime.Parse((string)r.timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Quality = Enum.Parse<QualityCode>((string)r.quality),
                ErrorMessage = r.error_msg as string
            }).ToList();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据查询失败");
        }
    }

    public async Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
    {
        // ADR-002 P1-1：清理异常统一走 SqliteErrorClassifier，返回 OperationResult 而非向调用方抛异常
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);
            await conn.ExecuteAsync("DELETE FROM measurements WHERE timestamp < @before", new { before = before.ToUniversalTime().ToString("O") });
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据清理失败");
        }
    }

    /// <summary>
    /// 解析存储的 data_type 字符串。
    /// ADR-002 P1-3 修复前旧数据该列为空串，真实类型无法恢复，回退默认值。
    /// </summary>
    private static DataType ParseDataType(string? value)
        => Enum.TryParse<DataType>(value, ignoreCase: true, out var type) ? type : default;

    private string? Serialize(object? raw)
    {
        if (raw is null) return null;
        if (raw is ushort[] regs) return JsonSerializer.Serialize(regs, _json);
        return JsonSerializer.Serialize(raw, _json);
    }

    private object? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ushort[]>(json, _json); }
        catch { return json; }
    }
}
