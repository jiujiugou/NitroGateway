using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 创建设备/点位表。
/// 历史命名说明（ADR-002 P3-1）：本迁移列名为 PascalCase（Id/Name/DeviceId...），
/// 与后续 M001/M002/M004~M006 的 snake_case 不一致，但属于已执行的迁移，
/// 改动列名会破坏既有数据库结构，故保留现状不再修改；新增表统一 snake_case。
/// 防御性 Schema.Exists 判断（幂等迁移机制已保证）同样保留，不额外清理。
/// </summary>
[Migration(3)]
public sealed class M003_CreateDeviceTables : Migration
{
    /// <summary>
    /// 正向：建 devices/points 表 + 点位设备外键索引；
    /// 防御性跳过已存在的表（幂等迁移机制下通常不会触发）。
    /// </summary>
    public override void Up()
    {
        if (Schema.Table("devices").Exists()) return;
        Create.Table("devices")
            .WithColumn("Id").AsString().PrimaryKey()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("ProtocolName").AsString(100).NotNullable()
            .WithColumn("ProtocolDialect").AsString(100).Nullable()
            .WithColumn("Endpoint").AsString(500).NotNullable()
            .WithColumn("ConnectTimeoutMs").AsInt32().WithDefaultValue(3000)
            .WithColumn("RequestTimeoutMs").AsInt32().WithDefaultValue(5000)
            .WithColumn("RetryCount").AsInt32().WithDefaultValue(3)
            .WithColumn("Status").AsString(50).NotNullable()
            .WithColumn("ConnectionParams").AsString().Nullable();

        Create.Table("points")
            .WithColumn("Id").AsString().PrimaryKey()
            .WithColumn("DeviceId").AsString().NotNullable().ForeignKey("devices", "Id")
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("Address").AsString(200).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("DataType").AsString(50).NotNullable()
            .WithColumn("Access").AsString(50).NotNullable().WithDefaultValue("ReadOnly")
            .WithColumn("Enabled").AsBoolean().WithDefaultValue(true)
            .WithColumn("ScanIntervalMs").AsInt32().WithDefaultValue(0)
            .WithColumn("Deadband").AsDouble().WithDefaultValue(0)
            .WithColumn("ScaleFactor").AsDouble().WithDefaultValue(1.0)
            .WithColumn("ScaleOffset").AsDouble().WithDefaultValue(0);

        Create.Index("IX_points_DeviceId").OnTable("points").OnColumn("DeviceId");
    }

    /// <summary>回滚：先删 points 再删 devices（外键顺序）</summary>
    public override void Down()
    {
        Delete.Table("points");
        Delete.Table("devices");
    }
}
