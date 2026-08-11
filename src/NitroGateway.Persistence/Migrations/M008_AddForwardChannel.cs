using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// forward_buffer 增加通道列（ADR-011 多通道转发）。
/// MQTT 与 HTTP 两条北向通道共用缓冲，按 channel 隔离出队；旧数据默认 'mqtt' 无需迁移。
/// </summary>
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
