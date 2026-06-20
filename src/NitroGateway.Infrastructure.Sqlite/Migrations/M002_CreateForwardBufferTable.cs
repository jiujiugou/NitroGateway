using FluentMigrator;

namespace NitroGateway.Infrastructure.Sqlite.Migrations;

/// <summary>创建 forward_buffer 转发缓冲表</summary>
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
