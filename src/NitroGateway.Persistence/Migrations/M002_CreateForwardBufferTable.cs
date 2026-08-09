using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 创建 forward_buffer 转发缓冲表。
/// payload 为 BatchMeasurements 的 CamelCase JSON；status 生命周期 Pending → InFlight →（删除/DeadLetter）；
/// 索引 (status, enqueued_at) 支撑按状态+FIFO 顺序出队。
/// </summary>
[Migration(2)]
public sealed class M002_CreateForwardBufferTable : Migration
{
    /// <summary>正向：建表 + 状态/FIFO 索引</summary>
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

    /// <summary>回滚：删表</summary>
    public override void Down() => Delete.Table("forward_buffer");
}
