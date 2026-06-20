# Storage 实现设计文档

## 原则

- 边缘端默认 SQLite，云端可替换为 PostgreSQL / InfluxDB / TimescaleDB
- 实现放在独立的 Infrastructure 项目，Storage 接口项目保持零重量级依赖
- 每个实现扛自己的 NuGet 包，互不污染

---

## 项目结构

```
NitroGateway.Storage/                        ← 只有接口，无实现
├── Configuration/
│   ├── IDeviceRepository.cs
│   └── IPointRepository.cs
├── TimeSeries/
│   └── IMeasurementStore.cs
└── Buffer/
    └── IForwardBuffer.cs

NitroGateway.Infrastructure.Sqlite/         ← SQLite 实现（新增）
├── NitroGateway.Infrastructure.Sqlite.csproj
├── NitroGatewayDbContext.cs                 EF Core DbContext（Configuration）
├── SqliteDeviceRepository.cs
├── SqlitePointRepository.cs
├── SqliteMeasurementStore.cs
├── SqliteForwardBuffer.cs
├── Migrations/                               FluentMigrator 迁移（TimeSeries + Buffer）
│   ├── M001_CreateMeasurementsTable.cs
│   └── M002_CreateForwardBufferTable.cs
├── SqliteServiceCollectionExtensions.cs    DI：AddNitroSqlite()
└── MigrationRunner.cs                      启动时执行迁移
```

---

## Configuration — EF Core + SQLite

### 数据模型（EF 实体）

```csharp
// 设备表
public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public string ProtocolName { get; set; }       // ProtocolIdentifier.Name
    public string? ProtocolDialect { get; set; }   // ProtocolIdentifier.Dialect
    public string Endpoint { get; set; }           // DeviceConnection.Endpoint
    public int ConnectTimeoutMs { get; set; }
    public int RequestTimeoutMs { get; set; }
    public int RetryCount { get; set; }
    public string Status { get; set; }             // DeviceStatus 字符串
    public string? ConnectionParams { get; set; }  // JSON，序列化 Parameters
    public ICollection<PointEntity> Points { get; set; }
}

// 点位表
public sealed class PointEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Name { get; set; }
    public string Address { get; set; }
    public string? Description { get; set; }
    public string DataType { get; set; }           // DataType 字符串
    public string Access { get; set; }             // PointAccess 字符串
    public bool Enabled { get; set; }
    public int ScanIntervalMs { get; set; }
    public double Deadband { get; set; }
    public double ScaleFactor { get; set; }
    public double ScaleOffset { get; set; }
    public DeviceEntity Device { get; set; }
}
```

### Repository 实现

`SqliteDeviceRepository` — 领域实体 ↔ EF 实体的映射在 Repository 内部完成：

```
Domain.Device ←→ DeviceEntity (Id/Name/Protocol/ConnectionParams 拆解/拼装)
Domain.DevicePoint ←→ PointEntity
```

查询示例：

```csharp
public async Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct)
{
    var entity = await _db.Devices
        .Include(d => d.Points)
        .FirstOrDefaultAsync(d => d.Id == deviceId, ct);
    if (entity is null)
        return OperationalError.General("设备不存在");
    return MapToDomain(entity);
}
```

### DI

```csharp
services.AddNitroSqlite("Data Source=nitrogateway.db");
// 内部注册 DbContext + SqliteDeviceRepository + SqlitePointRepository
```

---

## TimeSeries — SQLite 批量写入

### 策略

时序数据的特点是**海量追加、极少删除、按时间范围查询**。不用 EF Core，用 `Microsoft.Data.Sqlite` 手工 SQL 写入。
Schema 迁移由 FluentMigrator 管理。

### 迁移（FluentMigrator）

```csharp
[Migration(1)]
public sealed class M001_CreateMeasurementsTable : Migration
{
    public override void Up()
    {
        Create.Table("measurements")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("device_id").AsString().NotNullable()
            .WithColumn("point_id").AsString().NotNullable()
            .WithColumn("point_name").AsString().NotNullable()
            .WithColumn("raw_value").AsString().Nullable()
            .WithColumn("value").AsDouble().Nullable()
            .WithColumn("data_type").AsString().NotNullable()
            .WithColumn("timestamp").AsString().NotNullable()
            .WithColumn("quality").AsString().NotNullable()
            .WithColumn("error_msg").AsString().Nullable();

        Create.Index("idx_measurements_query")
            .OnTable("measurements")
            .OnColumn("device_id").Ascending()
            .OnColumn("point_id").Ascending()
            .OnColumn("timestamp").Ascending();
    }

    public override void Down() => Delete.Table("measurements");
}
```

### 写入

```csharp
public async Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct)
{
    await using var tx = await _connection.BeginTransactionAsync(ct);
    foreach (var s in snapshots)
    {
        // 参数化 INSERT
        _insertCmd.Parameters["@id"].Value = Guid.NewGuid().ToString();
        _insertCmd.Parameters["@device_id"].Value = s.DeviceId.ToString();
        // ...
        await _insertCmd.ExecuteNonQueryAsync(ct);
    }
    await tx.CommitAsync(ct);
}
```

### 查询

```csharp
SELECT value, timestamp, quality
FROM measurements
WHERE device_id = @did AND point_id = @pid
  AND timestamp BETWEEN @from AND @to
ORDER BY timestamp ASC
```

### 清理

```csharp
DELETE FROM measurements WHERE timestamp < @before
```

---

## Buffer — SQLite 作为 FIFO 持久队列

### 策略

利用 SQLite 的表作为持久队列。核心字段：

| 字段 | 说明 |
|---|---|
| `id` | 批次 ID |
| `payload` | JSON 序列化的 BatchMeasurements |
| `status` | Pending（待转发）/ Sent（已确认，可删除） |
| `enqueued_at` | 入队时间 |

### 迁移（FluentMigrator）

```csharp
[Migration(2)]
public sealed class M002_CreateForwardBufferTable : Migration
{
    public override void Up()
    {
        Create.Table("forward_buffer")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("payload").AsString().NotNullable()
            .WithColumn("status").AsString().NotNullable().WithDefaultValue("Pending")
            .WithColumn("enqueued_at").AsString().NotNullable();

        Create.Index("idx_forward_buffer_status")
            .OnTable("forward_buffer")
            .OnColumn("status").Ascending()
            .OnColumn("enqueued_at").Ascending();
    }

    public override void Down() => Delete.Table("forward_buffer");
}
```

### 操作

```csharp
// EnqueueAsync → INSERT
INSERT INTO forward_buffer (id, payload, status, enqueued_at) VALUES (...);

// DequeueAsync → SELECT (不移除)
SELECT id, payload FROM forward_buffer WHERE status = 'Pending'
ORDER BY enqueued_at ASC LIMIT @maxCount;

// CommitAsync → 转发成功后删除
DELETE FROM forward_buffer WHERE id IN (...)

// Count
SELECT COUNT(*) FROM forward_buffer WHERE status = 'Pending'
```

### 断电不丢

SQLite WAL 模式默认开启，写入即持久化，掉电重启后数据完整。

---

## 迁移执行

```csharp
// MigrationRunner.cs — 启动时按顺序执行
public static class MigrationRunner
{
    public static void Run(string connectionString)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        var runner = new FluentMigrator.Runner.MigrationRunner(
            typeof(M001_CreateMeasurementsTable).Assembly);
        runner.MigrateUp();
    }
}
```

调用：

```csharp
services.AddNitroSqlite("Data Source=/data/nitrogateway.db");
MigrationRunner.Run(connectionString);  // 启动时执行，幂等（已执行的跳过）
```

## DI 总入口

```csharp
// Program.cs
services.AddNitroSqlite("Data Source=/data/nitrogateway.db");
// 等效于：
//   services.AddDbContext<NitroGatewayDbContext>(...);
//   services.AddSingleton<IDeviceRepository, SqliteDeviceRepository>();
//   services.AddSingleton<IPointRepository, SqlitePointRepository>();
//   services.AddSingleton<IMeasurementStore, SqliteMeasurementStore>();
//   services.AddSingleton<IForwardBuffer, SqliteForwardBuffer>();
```

---

## NuGet 依赖

| 用途 | 包 |
|---|---|
| Entity Framework Core | `Microsoft.EntityFrameworkCore.Sqlite` |
| 手工 SQL（时序 + Buffer 写操作） | `Microsoft.Data.Sqlite` |
| Schema 迁移（时序 + Buffer） | `FluentMigrator` + `FluentMigrator.Runner.SQLite` |

---

## 约束

1. EF Core 只用于 Configuration（设备+点位），TimeSeries 和 Buffer 走裸 SQL
2. 每个 Repository 在构造函数接收连接字符串，不自作主张创建数据库文件
3. 迁移由启动入口负责（`dbContext.Database.EnsureCreated()`）
4. TimeSeries 写入使用事务批处理，但单批不超过 1000 条以避免锁表
5. Buffer 的 Dequeue + Commit 是两阶段，Forwarder 未确认前数据不删
