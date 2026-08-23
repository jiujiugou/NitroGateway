using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 用户表（ADR-066：用户 DB 化，不走全量 Identity）。users 承载运行时账号，
/// 配置文件（Security:Users）仅作首启种子（空表灌入），此后新增/改密/启停都落库、即时生效。
/// <para>列约定沿用 M014：时间列 O 格式字符串（UTC），字符串比较与时间序一致；</para>
/// <para>密码只存 PasswordHasher 哈希（与配置用户同格式），绝不存明文。</para>
/// </summary>
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
