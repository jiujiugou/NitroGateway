using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 设备站点归属（ADR-035 方案 A，2026-08-12 拍板）：devices 加 SiteId 列，设备归属单一站点。
/// 中心导出/下发按 site_id 过滤（现场只拿到本站点设备，避免跨现场配置互相污染）；
/// 现场上报（ConfigSync push）以请求方站点标识写入本列（上报方即归属方）。
/// 旧数据默认空串（未标注站点）——单现场既有部署不受影响；Web 建设备时需显式指定站点才能下发到现场。
/// 列名沿用 devices 表的 PascalCase 历史（M003 说明），与 UpdatedAt/IsDeleted 一致。
/// </summary>
[Migration(11)]
public sealed class M011_AddDeviceSiteColumn : Migration
{
    /// <summary>正向：devices 加 SiteId 列（默认空串，旧行归"未标注站点"）</summary>
    public override void Up()
    {
        Alter.Table("devices")
            .AddColumn("SiteId").AsString(100).NotNullable().WithDefaultValue("");
    }

    /// <summary>回滚：删除 SiteId 列</summary>
    public override void Down()
    {
        Delete.Column("SiteId").FromTable("devices");
    }
}
