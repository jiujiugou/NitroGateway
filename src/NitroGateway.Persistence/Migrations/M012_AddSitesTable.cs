using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 站点注册表（ADR-036，2026-08-12）：site_id 唯一索引兜底多现场隔离。
/// Ingest 首见站点写入（upsert），后续只更新 last_seen；source_client_id/last_seen_client_id
/// 记录 MQTT 来源指纹（ClientId 含机器名），供后续"同一 siteId 多来源"冲突检测。
/// Web 站点列表 = sites ∪ measurements ∪ alarms 去重（兼容既有库，站点不因升级消失）。
/// </summary>
[Migration(12)]
public sealed class M012_AddSitesTable : Migration
{
    /// <summary>正向：sites 建表，site_id 唯一约束</summary>
    public override void Up()
    {
        Create.Table("sites")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("site_id").AsString(32).NotNullable().Unique()
            .WithColumn("display_name").AsString(100).NotNullable().WithDefaultValue("")
            .WithColumn("source_client_id").AsString(200).Nullable()
            .WithColumn("last_seen_client_id").AsString(200).Nullable()
            .WithColumn("first_seen_at").AsDateTime().NotNullable()
            .WithColumn("last_seen_at").AsDateTime().NotNullable();
    }

    /// <summary>回滚：删除 sites 表</summary>
    public override void Down()
    {
        Delete.Table("sites");
    }
}