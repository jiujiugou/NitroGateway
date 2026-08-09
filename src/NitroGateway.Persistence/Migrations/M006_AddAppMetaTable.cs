using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 创建 app_meta 元数据表：key-value 键值对存储（当前仅 app_version），
/// updated_at 记录最近写入时间。由 MigrationRunner 在迁移完成后写入应用版本。
/// </summary>
[Migration(6)]
public sealed class M006_AddAppMetaTable : Migration
{
    /// <summary>正向：建 app_meta 键值表</summary>
    public override void Up()
    {
        Create.Table("app_meta")
            .WithColumn("key").AsString(100).PrimaryKey()
            .WithColumn("value").AsString().NotNullable()
            .WithColumn("updated_at").AsString().NotNullable();
    }

    /// <summary>回滚：删表</summary>
    public override void Down()
    {
        Delete.Table("app_meta");
    }
}
