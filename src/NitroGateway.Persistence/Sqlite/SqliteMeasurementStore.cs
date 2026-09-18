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

public sealed class SqliteMeasurementStore : IMeasurementStore
{
    private const int DefaultPurgeBatchSize = 10_000;

    private readonly string _connectionString;
    private readonly int _purgeBatchSize;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// 以连接串构造；连接在每次操作内按需创建，不持有长连接。
    /// </summary>
    /// <param name="connString">SQLite 连接串</param>
    /// <param name="purgeBatchSize">保留清理单批删除行数，最小 1（测试可注入小值验证分批行为）</param>
    public SqliteMeasurementStore(string connString, int purgeBatchSize = DefaultPurgeBatchSize)
    {
        _connectionString = connString;
        _purgeBatchSize = Math.Max(1, purgeBatchSize);
    }

    /// <summary>
    /// 批量写入快照（单事务，一次 ExecuteAsync 批量 INSERT）。
    /// raw_value 以 JSON 存储（寄存器数组等复合类型）；value 统一转 double（不可转换存 NULL）；
    /// 写入带 Activity 追踪（<see cref="GatewayActivities.SqliteWrite"/>），失败时置 Error 状态并带错误标签。
    /// 空列表直接成功返回。异常回滚后归类返回，不抛出。
    /// </summary>
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
                    name = s.PointName ?? string.Empty,
                    raw = Serialize(s.RawValue),
                    // value 列只承载"能转 double"的工程值（Bool→1/0、数值、数字文本）。
                    // 不可转的（非数字 String、DateTime、结构体等）落 NULL、真值留在 raw_value，
                    // 绝不因单点格式抛异常拖垮整批 INSERT（Convert.ToDouble 对 "abc"/DateTime 会抛，
                    // 一旦抛即回滚同批所有点位的历史落库——含纯数值点）。
                    val = TryConvertToDouble(s.Value, out var dbl) ? dbl : (object)DBNull.Value,
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

    /// <summary>
    /// 按设备+点位+时间范围查询历史快照（timestamp 升序）。
    /// 时间参数转 UTC O 格式字符串做范围比较；查询异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
    {
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
    /// 按设备+时间范围查询该设备下全部点位的快照（timestamp 倒序，最新在前）。
    /// 供"批量取最新值"类场景使用；异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
    {
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

    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
        => await QueryPagedAsync(deviceId, pointId, from, to, limit, offset, null, ct);

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, string? siteId,
        CancellationToken ct = default)
    {
        try
        {
            var safeLimit = Math.Clamp(limit, 1, 1000);
            var safeOffset = Math.Max(0, offset);
            // ADR-035 第 1 步：siteId 非空时按站点过滤（中心库多站点场景）
            var siteClause = string.IsNullOrEmpty(siteId) ? "" : " AND site_id = @site";

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var sql = pointId.HasValue
                ? $@"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND point_id = @pid AND timestamp BETWEEN @from AND @to{siteClause}
                    ORDER BY timestamp ASC LIMIT @limit OFFSET @offset"
                : $@"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND timestamp BETWEEN @from AND @to{siteClause}
                    ORDER BY timestamp ASC LIMIT @limit OFFSET @offset";

            var rows = await conn.QueryAsync(sql, new
            {
                did = deviceId.ToString(),
                pid = pointId?.ToString(),
                site = siteId,
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

    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default)
        => await QueryLatestAsync(deviceId, pointId, null, ct);

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, string? siteId, CancellationToken ct = default)
    {
        try
        {
            // ADR-035 第 1 步：siteId 非空时按站点过滤（中心库多站点场景）
            var siteClause = string.IsNullOrEmpty(siteId) ? "" : " AND site_id = @site";

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            SqlitePragmas.Apply(conn);

            var sql = pointId.HasValue
                ? $@"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM measurements WHERE device_id = @did AND point_id = @pid{siteClause}
                    ORDER BY timestamp DESC LIMIT 1"
                :
                  // 原 join 在同点位两条记录 timestamp 相同时会返回多行，"每点最新一条"不成立
                  $@"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM (
                        SELECT m.*, ROW_NUMBER() OVER (PARTITION BY point_id ORDER BY timestamp DESC) AS rn
                        FROM measurements m
                        WHERE device_id = @did{siteClause}
                    ) ranked
                    WHERE ranked.rn = 1";

            var rows = await conn.QueryAsync(sql,
                new { did = deviceId.ToString(), pid = pointId?.ToString(), site = siteId });

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
        try
        {
            var cutoff = before.ToUniversalTime().ToString("O");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                SqlitePragmas.Apply(conn);
                await using var tx = await conn.BeginTransactionAsync(ct);

                var ids = (await conn.QueryAsync<string>(
                    "SELECT id FROM measurements WHERE timestamp < @before LIMIT @batch",
                    new { before = cutoff, batch = _purgeBatchSize }, tx)).ToList();
                if (ids.Count == 0) break;

                await conn.ExecuteAsync(
                    "DELETE FROM measurements WHERE id IN @ids",
                    new { ids }, tx);
                await tx.CommitAsync(ct);

                // 本批未删满说明已清空目标行，退出循环
                if (ids.Count < _purgeBatchSize) break;
            }
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return SqliteErrorClassifier.Classify(ex, "时序数据清理失败");
        }
    }

    private static DataType ParseDataType(string? value)
        => Enum.TryParse<DataType>(value, ignoreCase: true, out var type) ? type : default;

    /// <summary>
    /// 值 → double（value 列工程值）：Bool/数值/可解析数字文本转成功；
    /// null 与不可转（非数字文本、DateTime、结构体等）返回 false → 调用方落 NULL。
    /// 与 <c>ChangeDetector</c> 同构：宁可单点落 NULL，也不让格式问题中断整批时序写入。
    /// </summary>
    private static bool TryConvertToDouble(object? value, out double result)
    {
        result = 0;
        if (value is null) return false;
        try
        {
            result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 原始值序列化：寄存器数组（ushort[]）与普通对象统一转 CamelCase JSON；null 存 NULL。
    /// </summary>
    private string? Serialize(object? raw)
    {
        if (raw is null) return null;
        if (raw is ushort[] regs) return JsonSerializer.Serialize(regs, _json);
        return JsonSerializer.Serialize(raw, _json);
    }

    /// <summary>
    /// 原始值反序列化：优先按寄存器数组（ushort[]）解析，失败则原样返回字符串
    /// （兼容历史写入的标量/未知结构，避免读历史数据抛异常）。
    /// </summary>
    private object? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ushort[]>(json, _json); }
        catch { return json; }
    }
}
