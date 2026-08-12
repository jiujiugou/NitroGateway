using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 配置同步字段（ADR-033 阶段 3/4）：devices/points 加 UpdatedAt（同步版本依据）与
/// IsDeleted（中心侧 tombstone，拒绝现场复活）；新建 config_sync_outbox（现场待上报变更队列，
/// 中心侧该表保持空置，仅现场写）。
/// 旧数据 UpdatedAt 默认空串（等价"最旧"），IsDeleted 默认 0——首次下发时中心版本会覆盖旧配置。
/// </summary>
[Migration(10)]
public sealed class M010_AddConfigSyncColumns : Migration
{
    /// <summary>正向：加列 + 建 outbox 表</summary>
    public override void Up()
    {
        Alter.Table("devices")
            .AddColumn("UpdatedAt").AsString().NotNullable().WithDefaultValue("")
            .AddColumn("IsDeleted").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("points")
            .AddColumn("UpdatedAt").AsString().NotNullable().WithDefaultValue("")
            .AddColumn("IsDeleted").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Table("config_sync_outbox")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("entity_type").AsString(30).NotNullable()
            .WithColumn("device_id").AsString().NotNullable()
            .WithColumn("point_id").AsString().Nullable()
            .WithColumn("updated_at").AsString().NotNullable();
    }

    /// <summary>回滚：删表 + 删列</summary>
    public override void Down()
    {
        Delete.Table("config_sync_outbox");
        Delete.Column("IsDeleted").FromTable("points");
        Delete.Column("UpdatedAt").FromTable("points");
        Delete.Column("IsDeleted").FromTable("devices");
        Delete.Column("UpdatedAt").FromTable("devices");
    }
}
