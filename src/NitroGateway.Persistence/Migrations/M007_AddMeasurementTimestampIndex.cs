using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>
/// measurements 表 timestamp 单列索引（ADR-018 P2-1）。
/// 保留清理（<c>PurgeAsync</c>）按 timestamp 前缀分批删除，无单列索引时每批 DELETE 都全表扫描；
/// 复合索引 (device_id, point_id, timestamp) 的 timestamp 只在左前缀匹配（device_id/point_id 命中）时可用，
/// 无法支撑"按时间全表范围删除"的清理路径。
/// </summary>
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
