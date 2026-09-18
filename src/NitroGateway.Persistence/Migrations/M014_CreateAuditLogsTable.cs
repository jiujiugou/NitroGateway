using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

[Migration(14)]
public sealed class M014_CreateAuditLogsTable : Migration
{
    /// <summary>正向：建 audit_logs 表 + 按时间倒序索引（历史查询页走此索引）</summary>
    public override void Up()
    {
        Create.Table("audit_logs")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("user").AsString().NotNullable()
            .WithColumn("role").AsString(50).NotNullable()
            .WithColumn("method").AsString(10).NotNullable()
            .WithColumn("path").AsString(500).NotNullable()
            .WithColumn("status_code").AsInt32().NotNullable()
            .WithColumn("elapsed_ms").AsInt32().NotNullable()
            .WithColumn("ip").AsString(64).NotNullable()
            .WithColumn("created_at").AsString().NotNullable();

        Create.Index("idx_audit_logs_created")
            .OnTable("audit_logs")
            .OnColumn("created_at").Descending();
    }

    /// <summary>回滚：删除审计表</summary>
    public override void Down()
    {
        Delete.Table("audit_logs");
    }
}
