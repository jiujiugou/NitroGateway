using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

[Migration(15)]
public sealed class M015_CreateUsersTable : Migration
{
    /// <summary>正向：建 users 表 + username 唯一约束（大小写敏感，登录 Trim 后精确匹配）</summary>
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("username").AsString(64).NotNullable().Unique()
            .WithColumn("password_hash").AsString(512).NotNullable()
            .WithColumn("role").AsString(50).NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsString().NotNullable()
            .WithColumn("updated_at").AsString().NotNullable()
            .WithColumn("last_login_at").AsString().Nullable();
    }

    /// <summary>回滚：删除 users 表</summary>
    public override void Down()
    {
        Delete.Table("users");
    }
}
