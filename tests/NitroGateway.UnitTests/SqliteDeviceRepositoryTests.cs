using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NitroGateway.Domain.Devices;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// SqliteDeviceRepository 异常分类测试（ADR-018 P2-2）：
/// EF/Sqlite 异常归类为 OperationResult 而非冒泡；DomainMapper 枚举容错（ADR-018 P3-4）。
/// </summary>
public class SqliteDeviceRepositoryTests
{
    /// <summary>临时文件库：按 M003 迁移结构建 devices/points 表，释放时删除文件。</summary>
    private sealed class TempDeviceDb : IDisposable
    {
        public string ConnectionString { get; }

        private readonly string _path;

        public TempDeviceDb()
        {
            _path = Path.Combine(Path.GetTempPath(), $"ntg-dev-{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={_path};Pooling=False";
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE devices (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NULL,
                    ProtocolName TEXT NOT NULL,
                    ProtocolDialect TEXT NULL,
                    Endpoint TEXT NOT NULL,
                    ConnectTimeoutMs INTEGER NOT NULL DEFAULT 3000,
                    RequestTimeoutMs INTEGER NOT NULL DEFAULT 5000,
                    RetryCount INTEGER NOT NULL DEFAULT 3,
                    Status TEXT NOT NULL,
                    ConnectionParams TEXT NULL,
                    UpdatedAt TEXT NOT NULL DEFAULT '',
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    SiteId TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE points (
                    Id TEXT PRIMARY KEY,
                    DeviceId TEXT NOT NULL REFERENCES devices(Id),
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

    private NitroGatewayDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<NitroGatewayDbContext>().UseSqlite(connectionString).Options);

    private static Device NewDevice(Guid id, string name = "PLC") => new()
    {
        Id = id,
        Name = name,
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "127.0.0.1:502" },
        Status = DeviceStatus.Online
    };

    /// <summary>ADR-018 P2-2：约束违反（NOT NULL）归类为 Storage 失败而非抛异常</summary>
    [Fact]
    public async Task SaveAsync_NotNullViolation_ReturnsClassifiedFailure()
    {
        using var db = new TempDeviceDb();
        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqliteDeviceRepository(context);

        var device = NewDevice(Guid.NewGuid());
        device.Name = null!;   // 违反 devices.Name NOT NULL

        var result = await repo.SaveAsync(device);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }

    /// <summary>ADR-018 P2-2：表缺失时查询/删除返回分类失败而非抛异常</summary>
    [Fact]
    public async Task GetById_TableMissing_ReturnsClassifiedFailure()
    {
        using var db = new TempDeviceDb();
        using (var conn = new SqliteConnection(db.ConnectionString))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = "DROP TABLE devices";
            command.ExecuteNonQuery();
        }

        await using var context = CreateContext(db.ConnectionString);
        var repo = new SqliteDeviceRepository(context);

        var result = await repo.GetByIdAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }

    /// <summary>ADR-018 P3-4：Status 列是未知枚举字符串时回退 Unknown，不抛异常</summary>
    [Fact]
    public async Task GetById_UnknownStatusString_FallsBackToUnknown()
    {
        using var db = new TempDeviceDb();
        var id = Guid.NewGuid();

        await using var context = CreateContext(db.ConnectionString);
        // 经 EF 落库一条 Status 为未知枚举字符串的设备（模拟历史/脏数据）
        context.Devices.Add(new DeviceEntity
        {
            Id = id,
            Name = "PLC",
            ProtocolName = "Modbus",
            Endpoint = "127.0.0.1:502",
            Status = "Bogus"
        });
        await context.SaveChangesAsync();

        var repo = new SqliteDeviceRepository(context);

        var result = await repo.GetByIdAsync(id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(DeviceStatus.Unknown, result.Value!.Status);
    }
}







