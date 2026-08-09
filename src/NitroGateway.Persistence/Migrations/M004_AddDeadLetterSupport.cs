using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// forward_buffer 增加死信队列支持：retry_count（累计失败重试次数，达到上限进 DeadLetter）
/// 和 last_error（最近一次失败原因，用于死信展示与排查）。
/// </summary>
[Migration(4)]
public sealed class M004_AddDeadLetterSupport : Migration
{
    /// <summary>正向：追加 retry_count / last_error 两列</summary>
    public override void Up()
    {
        Alter.Table("forward_buffer")
            .AddColumn("retry_count").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("forward_buffer")
            .AddColumn("last_error").AsString().Nullable();
    }

    /// <summary>回滚：删除两列</summary>
    public override void Down()
    {
        Delete.Column("retry_count").FromTable("forward_buffer");
        Delete.Column("last_error").FromTable("forward_buffer");
    }
}
