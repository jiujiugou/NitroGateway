using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 写功能（docs/14，2026-08-22）：points 表新增写值范围字段 MinLimit/MaxLimit（nullable double）。
/// 供 WriteGuard.Range 校验（写值必须在 [MinLimit, MaxLimit] 内）与前端点位编辑表单录入；
/// null = 不限。沿用 M003 的 PascalCase 列名现状，仅新增列，不改既有列。
/// </summary>
[Migration(13)]
public sealed class M013_AddPointWriteRange : Migration
{
    /// <summary>正向：points 增加 MinLimit / MaxLimit 列（可空）</summary>
    public override void Up()
    {
        Alter.Table("points")
            .AddColumn("MinLimit").AsDouble().Nullable()
            .AddColumn("MaxLimit").AsDouble().Nullable();
    }

    /// <summary>回滚：删除新增列（先删 MaxLimit 再删 MinLimit）</summary>
    public override void Down()
    {
        Delete.Column("MaxLimit").FromTable("points");
        Delete.Column("MinLimit").FromTable("points");
    }
}
