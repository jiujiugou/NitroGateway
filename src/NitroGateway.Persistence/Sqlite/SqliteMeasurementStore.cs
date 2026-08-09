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

/// <summary>
/// SQLite 时序数据存储实现（Dapper）。
/// 单例注册：每个操作独立创建连接（见 ADR-001 P1-4），打开后应用统一 PRAGMA；
/// 写入走单事务批量 INSERT，读写/清理异常统一经 <see cref="SqliteErrorClassifier"/> 归类为 OperationResult。
/// 时间戳统一以 UTC 的 O 格式字符串存储，保证字典序即时间序。
/// </summary>
public sealed class SqliteMeasurementStore : IMeasurementStore
{
    /// <summary>保留清理单批删除行数上限（ADR-018 P2-1）：每批独立事务，批间让出写锁窗口。</summary>
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

    /// <summary>
    /// 按设备+点位+时间范围查询历史快照（timestamp 升序）。
    /// 时间参数转 UTC O 格式字符串做范围比较；查询异常归类返回，不抛出。
    /// </summary>
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

    /// <summary>
    /// 按设备+时间范围查询该设备下全部点位的快照（timestamp 倒序，最新在前）。
    /// 供"批量取最新值"类场景使用；异常归类返回，不抛出。
    /// </summary>
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
                : // ADR-018 P3-2：ROW_NUMBER 按 point_id 分区取最新行，替代 MAX(timestamp) join——
                  // 原 join 在同点位两条记录 timestamp 相同时会返回多行，"每点最新一条"不成立
                  @"SELECT device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg
                    FROM (
                        SELECT m.*, ROW_NUMBER() OVER (PARTITION BY point_id ORDER BY timestamp DESC) AS rn
                        FROM measurements m
                        WHERE device_id = @did
                    ) ranked
                    WHERE ranked.rn = 1";

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

    /// <summary>
    /// 删除指定时间之前的历史数据（用于存储空间管理/保留策略）。
    /// ADR-018 P2-1：分批删除（单批 ≤ <see cref="_purgeBatchSize"/> 行，每批独立事务），
    /// 避免单条大 DELETE 在 WAL 下长时间持有写锁阻塞 1s 采集热路径的落库写入；
    /// 配合 M007 的 timestamp 单列索引，每批删除走索引而非全表扫描。
    /// 注意：本 SQLite 编译版不支持 DELETE ... LIMIT（SQLITE_ENABLE_UPDATE_DELETE_LIMIT 未开启），
    /// 故用 SELECT id 限批 → 按 id 批量删除 实现分批。
    /// 异常归类返回，不抛出。
    /// </summary>
    public async Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
    {
        // ADR-002 P1-1：清理异常统一走 SqliteErrorClassifier，返回 OperationResult 而非向调用方抛异常
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

    /// <summary>
    /// 解析存储的 data_type 字符串。
    /// ADR-002 P1-3 修复前旧数据该列为空串，真实类型无法恢复，回退默认值。
    /// </summary>
    private static DataType ParseDataType(string? value)
        => Enum.TryParse<DataType>(value, ignoreCase: true, out var type) ? type : default;

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
