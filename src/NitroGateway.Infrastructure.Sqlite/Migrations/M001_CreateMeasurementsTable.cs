using FluentMigrator;

namespace NitroGateway.Infrastructure.Sqlite.Migrations;

/// <summary>创建 measurements 时序数据表</summary>
[Migration(1)]
public sealed class M001_CreateMeasurementsTable : Migration
{
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

    public override void Down() => Delete.Table("measurements");
}
