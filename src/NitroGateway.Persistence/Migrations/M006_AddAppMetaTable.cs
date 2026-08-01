using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>创建 app_meta 元数据表（版本号、迁移记录）</summary>
[Migration(6)]
public sealed class M006_AddAppMetaTable : Migration
{
    public override void Up()
    {
        Create.Table("app_meta")
            .WithColumn("key").AsString(100).PrimaryKey()
            .WithColumn("value").AsString().NotNullable()
            .WithColumn("updated_at").AsString().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("app_meta");
    }
}
