using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// SqlitePointRepository：单条/批量 upsert、UpdatedAt 自动盖章、双条件删除幂等、按设备查询、异常归类。
/// 使用临时文件库 + M003 结构的 devices/points 表，独立于真实仓储实现。
/// </summary>
public class SqlitePointRepositoryTests
{
    /// <summary>临时文件库：建 devices/points 表，释放时删文件。</summary>
    private sealed class TempPointDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempPointDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-point-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE devices (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                CREATE TABLE points (
                    Id TEXT PRIMARY KEY,
                    DeviceId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Address TEXT NOT NULL,
                    Description TEXT NULL,
                    DataType TEXT NOT NULL,
                    Access TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    ScanIntervalMs INTEGER NOT NULL DEFAULT 0,
                    Deadband REAL NOT NULL DEFAULT 0,
                    ScaleFactor REAL NOT NULL DEFAULT 1.0,
                    ScaleOffset REAL NOT NULL DEFAULT 0,
                    MinLimit REAL NULL,
                    MaxLimit REAL NULL,
                    UpdatedAt TEXT NOT NULL DEFAULT '',
                    IsDeleted INTEGER NOT NULL DEFAULT 0
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

    private static NitroGatewayDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<NitroGatewayDbContext>().UseSqlite(connectionString).Options);

    private static DevicePoint NewPoint(Guid id, string name = "Temp") => new()
    {
        Id = id,
        Name = name,
        Address = "40001",
        DataType = DataType.Float,
        Access = PointAccess.ReadWrite
    };

    // ── SaveAsync ──

    [Fact]
    public async Task SaveAsync_New_PersistsAndStampsUpdatedAt()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceId = Guid.NewGuid();
        var point = NewPoint(Guid.NewGuid());

        var result = await repo.SaveAsync(deviceId, point);

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(await context.Points.ToListAsync());
        Assert.Equal("Temp", stored.Name);
        Assert.Equal(deviceId, stored.DeviceId);
        Assert.NotEqual("", stored.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_Existing_UpdatesInPlace()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceId = Guid.NewGuid();
        var point = NewPoint(Guid.NewGuid());
        await repo.SaveAsync(deviceId, point);

        point.Name = "Temp2";
        var result = await repo.SaveAsync(deviceId, point);

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(await context.Points.ToListAsync());
        Assert.Equal("Temp2", stored.Name);
    }

    [Fact]
    public async Task SaveAsync_ExplicitUpdatedAt_Preserved()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var point = NewPoint(Guid.NewGuid());
        point.UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await repo.SaveAsync(Guid.NewGuid(), point);

        var stored = Assert.Single(await context.Points.ToListAsync());
        Assert.Equal("2020-01-01T00:00:00.0000000Z", stored.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_NullName_ReturnsClassifiedFailure()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var point = NewPoint(Guid.NewGuid());
        point.Name = null!;   // 违反 points.Name NOT NULL

        var result = await repo.SaveAsync(Guid.NewGuid(), point);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("点位保存失败", result.Error!.Message);
    }

    // ── SaveBatchAsync ──

    [Fact]
    public async Task SaveBatchAsync_Empty_SucceedsWithoutTouchingDb()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);

        var result = await repo.SaveBatchAsync(Guid.NewGuid(), []);

        Assert.True(result.IsSuccess);
        Assert.Empty(await context.Points.ToListAsync());
    }

    [Fact]
    public async Task SaveBatchAsync_InsertsNewAndUpdatesExisting()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceId = Guid.NewGuid();
        var existing = NewPoint(Guid.NewGuid(), "Old");
        await repo.SaveAsync(deviceId, existing);

        existing.Name = "Updated";
        var fresh = NewPoint(Guid.NewGuid(), "New");

        var result = await repo.SaveBatchAsync(deviceId, [existing, fresh]);

        Assert.True(result.IsSuccess);
        var stored = await context.Points.OrderBy(p => p.Name).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, p => p.Name == "Updated");
        Assert.Contains(stored, p => p.Name == "New");
    }

    [Fact]
    public async Task SaveBatchAsync_StampsUpdatedAt()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);

        await repo.SaveBatchAsync(Guid.NewGuid(), [NewPoint(Guid.NewGuid()), NewPoint(Guid.NewGuid())]);

        var stored = await context.Points.ToListAsync();
        Assert.All(stored, p => Assert.NotEqual("", p.UpdatedAt));
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task DeleteAsync_Existing_RemovesRow()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceId = Guid.NewGuid();
        var point = NewPoint(Guid.NewGuid());
        await repo.SaveAsync(deviceId, point);

        var result = await repo.DeleteAsync(deviceId, point.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(await context.Points.ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_Missing_IdempotentSuccess()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);

        var result = await repo.DeleteAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteAsync_WrongDevice_DoesNotDelete()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceId = Guid.NewGuid();
        var point = NewPoint(Guid.NewGuid());
        await repo.SaveAsync(deviceId, point);

        var result = await repo.DeleteAsync(Guid.NewGuid(), point.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(await context.Points.ToListAsync());
    }

    // ── GetByDeviceAsync ──

    [Fact]
    public async Task GetByDeviceAsync_FiltersByDeviceAndMaps()
    {
        using var db = new TempPointDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        await repo.SaveAsync(deviceA, NewPoint(Guid.NewGuid(), "A1"));
        await repo.SaveAsync(deviceA, NewPoint(Guid.NewGuid(), "A2"));
        await repo.SaveAsync(deviceB, NewPoint(Guid.NewGuid(), "B1"));

        var result = await repo.GetByDeviceAsync(deviceA);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value!, p => Assert.StartsWith("A", p.Name));
        Assert.Equal(DataType.Float, result.Value![0].DataType);
    }

    [Fact]
    public async Task GetByDeviceAsync_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempPointDb();
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "DROP TABLE points";
            command.ExecuteNonQuery();
        }
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqlitePointRepository(context);

        var result = await repo.GetByDeviceAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
        Assert.Contains("点位查询失败", result.Error!.Message);
    }
}
