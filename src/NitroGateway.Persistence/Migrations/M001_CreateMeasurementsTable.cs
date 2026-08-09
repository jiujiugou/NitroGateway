using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// 创建 measurements 时序数据表。
/// 单行即一次采集快照：设备/点位冗余标识 + 原始值与工程量值 + 时间戳（O 格式 UTC 字符串，
/// 字典序即时间序，便于字符串比较查询）+ 质量码与错误信息。
/// 复合索引 (device_id, point_id, timestamp) 支撑按设备+点位的时间范围查询。
/// </summary>
[Migration(1)]
public sealed class M001_CreateMeasurementsTable : Migration
{
    /// <summary>正向：建表 + 查询索引</summary>
    public override void Up()
    {
        Create.Table("measurements")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("device_id").AsString().NotNullable()
            .WithColumn("point_id").AsString().NotNullable()
            .WithColumn("point_name").AsString().NotNullable()
            .WithColumn("raw_value").AsString().Nullable()
            .WithColumn("value").AsDouble().Nullable()
            .WithColumn("data_type").AsString().NotNullable()
            .WithColumn("timestamp").AsString().NotNullable()
            .WithColumn("quality").AsString().NotNullable()
            .WithColumn("error_msg").AsString().Nullable();

        Create.Index("idx_measurements_query")
            .OnTable("measurements")
            .OnColumn("device_id").Ascending()
            .OnColumn("point_id").Ascending()
            .OnColumn("timestamp").Ascending();
    }

    /// <summary>回滚：删表</summary>
    public override void Down() => Delete.Table("measurements");
}
