using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// measurements / alarms 增加站点列（ADR-035 第 1 步）。
/// 中心库由 Ingest 按上行 topic 第三层（siteId）写入；现场库该列保持默认空串（本地数据不区分站点）。
/// 旧数据默认空串，Web 查询未指定 siteId 时不做过滤，兼容既有部署。
/// </summary>
[Migration(9)]
public sealed class M009_AddSiteIdColumns : Migration
{
    /// <summary>正向：两表追加 site_id 列（默认空串，旧行归入"未标注站点"）</summary>
    public override void Up()
    {
        Alter.Table("measurements").AddColumn("site_id").AsString(100).NotNullable().WithDefaultValue("");
        Alter.Table("alarms").AddColumn("site_id").AsString(100).NotNullable().WithDefaultValue("");
    }

    /// <summary>回滚：删除 site_id 列</summary>
    public override void Down()
    {
        Delete.Column("site_id").FromTable("measurements");
        Delete.Column("site_id").FromTable("alarms");
    }
}
