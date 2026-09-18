using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

[Migration(7)]
public sealed class M007_AddMeasurementTimestampIndex : Migration
{
    /// <summary>正向：建 timestamp 单列索引（O 格式 UTC 字符串，字典序即时间序）</summary>
    public override void Up()
    {
        Create.Index("idx_measurements_timestamp")
            .OnTable("measurements")
            .OnColumn("timestamp").Ascending();
    }

    /// <summary>回滚：删索引</summary>
    public override void Down() => Delete.Index("idx_measurements_timestamp").OnTable("measurements");
}
