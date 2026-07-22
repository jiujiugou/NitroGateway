using FluentMigrator;

namespace NitroGateway.Persistence.Migrations;

/// <summary>创建 alarm_rules 和 alarms 表</summary>
[Migration(5)]
public sealed class M005_AddAlarmTables : Migration
{
    public override void Up()
    {
        // ── 告警规则表 ──
        Create.Table("alarm_rules")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("device_id").AsString().NotNullable()
            .WithColumn("point_id").AsString().NotNullable()
            .WithColumn("operator").AsString(20).NotNullable()
            .WithColumn("threshold").AsDouble().NotNullable()
            .WithColumn("threshold_upper").AsDouble().Nullable()
            .WithColumn("duration_seconds").AsInt32().WithDefaultValue(0)
            .WithColumn("severity").AsString(20).NotNullable()
            .WithColumn("message_template").AsString().Nullable()
            .WithColumn("enabled").AsBoolean().WithDefaultValue(true);

        Create.Index("idx_alarm_rules_point")
            .OnTable("alarm_rules")
            .OnColumn("device_id").Ascending()
            .OnColumn("point_id").Ascending();

        // ── 告警记录表 ──
        Create.Table("alarms")
            .WithColumn("id").AsString().PrimaryKey()
            .WithColumn("rule_id").AsString().NotNullable()
            .WithColumn("device_id").AsString().NotNullable()
            .WithColumn("point_id").AsString().NotNullable()
            .WithColumn("trigger_value").AsDouble().Nullable()
            .WithColumn("threshold").AsDouble().Nullable()
            .WithColumn("severity").AsString(20).NotNullable()
            .WithColumn("message").AsString().WithDefaultValue("")
            .WithColumn("state").AsString(20).NotNullable()
            .WithColumn("first_exceeded_at").AsString().Nullable()
            .WithColumn("occurred_at").AsString().NotNullable()
            .WithColumn("acknowledged_at").AsString().Nullable()
            .WithColumn("resolved_at").AsString().Nullable();

        Create.Index("idx_alarms_device_state")
            .OnTable("alarms")
            .OnColumn("device_id").Ascending()
            .OnColumn("state").Ascending();

        Create.Index("idx_alarms_occurred")
            .OnTable("alarms")
            .OnColumn("occurred_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("alarms");
        Delete.Table("alarm_rules");
    }
}
