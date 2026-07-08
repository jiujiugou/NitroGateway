using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>forward_buffer 增加死信队列支持：retry_count 和 last_error 列</summary>
[Migration(4)]
public sealed class M004_AddDeadLetterSupport : Migration
{
    public override void Up()
    {
        Alter.Table("forward_buffer")
            .AddColumn("retry_count").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("forward_buffer")
            .AddColumn("last_error").AsString().Nullable();
    }

    public override void Down()
    {
        Delete.Column("retry_count").FromTable("forward_buffer");
        Delete.Column("last_error").FromTable("forward_buffer");
    }
}
