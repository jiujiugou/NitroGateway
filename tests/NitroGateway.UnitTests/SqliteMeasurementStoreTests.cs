using Microsoft.Data.Sqlite;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

public class SqliteMeasurementStoreTests
{
    /// <summary>临时文件库：按 M001 迁移结构建 measurements 表，释放时删除文件。</summary>
    private sealed class TempMeasurementDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempMeasurementDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-meas-{Guid.NewGuid():N}.db");
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
                    error_msg TEXT NULL
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

    private static PointSnapshot NewSnapshot(Guid deviceId, Guid pointId, DateTime timestamp) => new()
    {
        DeviceId = deviceId,
        DevicePointId = pointId,
        PointName = "T1",
        DataType = DataType.Float,
        Value = 36.6,
        Timestamp = timestamp,
        Quality = QualityCode.Good
    };

    /// <summary>P1-3：写入真实 point_name/data_type 而非空串，查询回填一致</summary>
    [Fact]
    public async Task WriteAsync_ThenQueryAsync_RoundtripsPointNameAndDataType()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var snapshot = NewSnapshot(deviceId, pointId, now);

        var write = await store.WriteAsync([snapshot]);
        Assert.True(write.IsSuccess);

        // 直接读库：确认落库的不是空串（P1-3 修复前为 ""）
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT point_name, data_type FROM measurements";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("T1", reader.GetString(0));
            Assert.Equal(nameof(DataType.Float), reader.GetString(1));
        }

        var result = await store.QueryAsync(deviceId, pointId, now.AddMinutes(-1), now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.Equal(snapshot.PointName, row.PointName);
        Assert.Equal(snapshot.DataType, row.DataType);
        Assert.Equal(snapshot.Value, row.Value);
        Assert.Equal(snapshot.Quality, row.Quality);
    }

    /// <summary>P1-3：按设备查询同样回填 point_name/data_type</summary>
    [Fact]
    public async Task QueryByDeviceAsync_ReturnsPointNameAndDataType()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await store.WriteAsync([NewSnapshot(deviceId, Guid.NewGuid(), now)]);

        var result = await store.QueryByDeviceAsync(deviceId, now.AddMinutes(-1), now.AddMinutes(1));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.Equal("T1", row.PointName);
        Assert.Equal(DataType.Float, row.DataType);
    }

    /// <summary>P1-3：修复前遗留的空串旧数据可正常读出，不回退为异常</summary>
    [Fact]
    public async Task QueryAsync_LegacyEmptyColumns_DoesNotThrow()
    {
        using var db = new TempMeasurementDb();
        var deviceId = Guid.NewGuid().ToString();
        var pointId = Guid.NewGuid().ToString();
        var ts = DateTime.UtcNow.ToString("O");

        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO measurements (id, device_id, point_id, point_name, raw_value, value, data_type, timestamp, quality, error_msg)
                VALUES (@id, @did, @pid, '', NULL, 1.0, '', @ts, 'Good', NULL)
                """;
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@did", deviceId);
            cmd.Parameters.AddWithValue("@pid", pointId);
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.ExecuteNonQuery();
        }

        var store = new SqliteMeasurementStore(db.ConnectionString);
        var result = await store.QueryAsync(Guid.Parse(deviceId), Guid.Parse(pointId), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.Equal("", row.PointName);
        Assert.Equal(default, row.DataType);
    }

    /// <summary>P1-2：PurgeAsync 删除保留边界之前的数据，边界之后保留</summary>
    [Fact]
    public async Task PurgeAsync_RemovesOldRows_KeepsRecent()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await store.WriteAsync([NewSnapshot(deviceId, pointId, now.AddDays(-40))]);
        await store.WriteAsync([NewSnapshot(deviceId, pointId, now.AddDays(-1))]);

        var purge = await store.PurgeAsync(now.AddDays(-30));
        Assert.True(purge.IsSuccess, purge.Error?.Message);

        var result = await store.QueryAsync(deviceId, pointId, now.AddDays(-45), now.AddDays(1));
        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(result.Value!);
        Assert.True((now.AddDays(-1) - row.Timestamp).Duration() < TimeSpan.FromMinutes(5));
    }

    /// <summary>P1-1：查询在表缺失时返回分类失败，不向调用方抛异常</summary>
    [Fact]
    public async Task QueryAsync_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempMeasurementDb();
        DropMeasurementsTable(db.ConnectionString);
        var store = new SqliteMeasurementStore(db.ConnectionString);

        var result = await store.QueryAsync(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("时序数据查询失败", result.Error.Message);
    }

    [Fact]
    public async Task QueryPagedAsync_LimitAndOffset_ReturnsSlice()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            await store.WriteAsync([NewSnapshot(deviceId, pointId, now.AddMinutes(i))]);

        var result = await store.QueryPagedAsync(deviceId, pointId, now.AddMinutes(-1), now.AddMinutes(10), limit: 2, offset: 1);

        Assert.True(result.IsSuccess);
        var rows = result.Value!;
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Timestamp < rows[1].Timestamp);
        Assert.True((rows[0].Timestamp - now.AddMinutes(1)).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task QueryPagedAsync_PointIdNull_QueriesWholeDevice()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await store.WriteAsync([
            NewSnapshot(deviceId, Guid.NewGuid(), now.AddMinutes(-2)),
            NewSnapshot(deviceId, Guid.NewGuid(), now.AddMinutes(-1))
        ]);

        var result = await store.QueryPagedAsync(deviceId, null, now.AddMinutes(-5), now.AddMinutes(1), limit: 10, offset: 0);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    /// <summary>P1-1：清理在表缺失时返回分类失败，不向调用方抛异常</summary>
    [Fact]
    public async Task PurgeAsync_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempMeasurementDb();
        DropMeasurementsTable(db.ConnectionString);
        var store = new SqliteMeasurementStore(db.ConnectionString);

        var result = await store.PurgeAsync(DateTime.UtcNow.AddDays(-30));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("时序数据清理失败", result.Error.Message);
    }

    [Fact]
    public async Task QueryLatestAsync_SinglePoint_ReturnsNewest()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await store.WriteAsync([NewSnapshot(deviceId, pointId, now.AddMinutes(-10))]);
        await store.WriteAsync([NewSnapshot(deviceId, pointId, now)]);

        var result = await store.QueryLatestAsync(deviceId, pointId);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.True((now - row.Timestamp).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task QueryLatestAsync_PointIdNull_ReturnsLatestPerPoint()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointA = Guid.NewGuid();
        var pointB = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await store.WriteAsync([NewSnapshot(deviceId, pointA, now.AddMinutes(-2))]);
        await store.WriteAsync([NewSnapshot(deviceId, pointA, now.AddMinutes(-1))]);
        await store.WriteAsync([NewSnapshot(deviceId, pointB, now.AddMinutes(-1))]);

        var result = await store.QueryLatestAsync(deviceId, pointId: null);

        Assert.True(result.IsSuccess);
        var rows = result.Value!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => r.DevicePointId).Distinct().Count());
        Assert.All(rows, r => Assert.True((now - r.Timestamp).Duration() < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task PurgeAsync_WithSmallBatchSize_DeletesAllOldRows_KeepsRecent()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString, purgeBatchSize: 2);
        var deviceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 5 行过期 + 1 行未过期，分批上限 2 强制走多轮循环
        for (var i = 0; i < 5; i++)
            await store.WriteAsync([NewSnapshot(deviceId, Guid.NewGuid(), now.AddDays(-40 - i))]);
        await store.WriteAsync([NewSnapshot(deviceId, Guid.NewGuid(), now.AddDays(-1))]);

        var purge = await store.PurgeAsync(now.AddDays(-30));
        Assert.True(purge.IsSuccess, purge.Error?.Message);

        var result = await store.QueryByDeviceAsync(deviceId, now.AddDays(-45), now);
        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!);
        Assert.True((now.AddDays(-1) - row.Timestamp).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task QueryLatestAsync_PointIdNull_SameTimestamp_DeduplicatesPerPoint()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var sameTimestamp = DateTime.UtcNow;

        // 同点位同时间戳写两条（修复前 MAX(timestamp) join 会返回多行）
        await store.WriteAsync([NewSnapshot(deviceId, pointId, sameTimestamp)]);
        await store.WriteAsync([NewSnapshot(deviceId, pointId, sameTimestamp)]);

        var result = await store.QueryLatestAsync(deviceId, pointId: null);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var row = Assert.Single(result.Value!);
        Assert.Equal(pointId, row.DevicePointId);
    }

    /// <summary>读某点位的 value / raw_value 列（未命中返回 null,null）</summary>
    private (double? Value, string? RawValue) ReadRow(SqliteConnection conn, Guid pointId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value, raw_value FROM measurements WHERE point_id = @pid";
        cmd.Parameters.AddWithValue("@pid", pointId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, null);
        var value = reader.IsDBNull(0) ? (double?)null : reader.GetDouble(0);
        var raw = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (value, raw);
    }

    /// <summary>
    /// 回归：非数字 String 点位不再把整批写入拖垮（Convert.ToDouble("abc") 曾抛 FormatException，
    /// 使同批数值点历史落库整体失败）。修复后该点落 NULL value、真值进 raw_value，批内数值点照常落库。
    /// </summary>
    [Fact]
    public async Task WriteAsync_StringWithNonNumericText_DoesNotKillBatch()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var floatPoint = Guid.NewGuid();
        var strPoint = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var write = await store.WriteAsync([
            NewSnapshot(deviceId, floatPoint, now),
            new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = strPoint,
                PointName = "S1",
                DataType = DataType.String,
                Value = "abc",
                RawValue = "abc",
                Timestamp = now,
                Quality = QualityCode.Good
            }
        ]);

        Assert.True(write.IsSuccess, write.Error?.Message);
        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        var (floatVal, _) = ReadRow(conn, floatPoint);
        Assert.Equal(36.6, floatVal);
        var (strVal, strRaw) = ReadRow(conn, strPoint);
        Assert.Null(strVal);                       // 不可转 → value 落 NULL
        Assert.Equal("\"abc\"", strRaw);            // 真值保留在 raw_value（JSON）
    }

    /// <summary>DateTime 等网关不可表示的对象值也不中断整批；该点 value 落 NULL、数值点不受影响</summary>
    [Fact]
    public async Task WriteAsync_UnsupportedObjectValue_DoesNotKillBatch()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var floatPoint = Guid.NewGuid();
        var otherPoint = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var write = await store.WriteAsync([
            NewSnapshot(deviceId, floatPoint, now),
            new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = otherPoint,
                PointName = "D1",
                DataType = DataType.String,
                // OPC UA DateTime 等真实类型透传：声明 String 但值是不可转 double 的对象
                Value = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                RawValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Timestamp = now,
                Quality = QualityCode.Good
            }
        ]);

        Assert.True(write.IsSuccess, write.Error?.Message);
        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        var (floatVal, _) = ReadRow(conn, floatPoint);
        Assert.Equal(36.6, floatVal);
        var (otherVal, _) = ReadRow(conn, otherPoint);
        Assert.Null(otherVal);
    }

    /// <summary>Bool 点位仍以数值 1/0 落 value 列（历史曲线/状态依赖此行为，修复不可回归）</summary>
    [Fact]
    public async Task WriteAsync_BoolValue_StoredAsNumericOneOrZero()
    {
        using var db = new TempMeasurementDb();
        var store = new SqliteMeasurementStore(db.ConnectionString);
        var deviceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await store.WriteAsync([
            new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = pointId,
                PointName = "B1",
                DataType = DataType.Bool,
                Value = true,
                RawValue = true,
                Timestamp = now,
                Quality = QualityCode.Good
            }
        ]);

        using var conn = new SqliteConnection(db.ConnectionString);
        conn.Open();
        var (value, _) = ReadRow(conn, pointId);
        Assert.Equal(1.0, value);
    }

    private static void DropMeasurementsTable(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = "DROP TABLE measurements";
        command.ExecuteNonQuery();
    }
}
