using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 操作审计落库表（ADR-065 A3）：audit_logs 记录 /api/* 非 GET 请求（写值/登录/配置变更）。
/// <para><b>为什么只落非 GET：</b>GET 是前端仪表盘 3-10s 高频轮询，落库会造成海量噪音行
/// （与 AuditMiddleware 中 GET 仅 Debug 日志同一原则，ADR-004 P3-3）；审计查询页聚焦
/// 「写值/登录/配置变更」类变更操作，非 GET（POST/PUT/DELETE/PATCH）正好覆盖。</para>
/// <para>时间列统一 O 格式字符串（UTC），字符串比较与时间序一致（沿用 M005 约定）。</para>
/// </summary>
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
