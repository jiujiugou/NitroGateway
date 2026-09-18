using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

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