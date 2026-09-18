using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

[Migration(8)]
public sealed class M008_AddForwardChannel : Migration
{
    /// <summary>正向：追加 channel 列（默认 'mqtt'，旧行自动归入 MQTT 通道）</summary>
    public override void Up()
    {
        Alter.Table("forward_buffer")
            .AddColumn("channel").AsString().NotNullable().WithDefaultValue("mqtt");
    }

    /// <summary>回滚：删除 channel 列</summary>
    public override void Down()
    {
        Delete.Column("channel").FromTable("forward_buffer");
    }
}
