using Microsoft.EntityFrameworkCore;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using Xunit;

namespace NitroGateway.UnitTests.Persistence;

/// <summary>
/// NitroGatewayDbContext.OnModelCreating 的映射契约：表名、列名（snake_case）、
/// 必填/长度约束、索引、外键级联。只构建模型，不连接数据库。
/// </summary>
public class NitroGatewayDbContextTests
{
    private static readonly NitroGatewayDbContext Context =
        new(new DbContextOptionsBuilder<NitroGatewayDbContext>().UseSqlite("Data Source=:memory:").Options);

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity<T>() => Context.Model.FindEntityType(typeof(T))!;

    [Fact]
    public void TableNames_AreMapped()
    {
        Assert.Equal("devices", Entity<DeviceEntity>().GetTableName());
        Assert.Equal("points", Entity<PointEntity>().GetTableName());
        Assert.Equal("alarms", Entity<AlarmEntity>().GetTableName());
        Assert.Equal("alarm_rules", Entity<AlarmRuleEntity>().GetTableName());
    }

    [Fact]
    public void Device_ColumnConstraints()
    {
        var e = Entity<DeviceEntity>();
        Assert.Equal("devices", e.GetTableName());
        Assert.Equal(200, e.FindProperty(nameof(DeviceEntity.Name))!.GetMaxLength());
        Assert.Equal(100, e.FindProperty(nameof(DeviceEntity.ProtocolName))!.GetMaxLength());
        Assert.Equal(100, e.FindProperty(nameof(DeviceEntity.ProtocolDialect))!.GetMaxLength());
        Assert.Equal(500, e.FindProperty(nameof(DeviceEntity.Endpoint))!.GetMaxLength());
        Assert.Equal(50, e.FindProperty(nameof(DeviceEntity.Status))!.GetMaxLength());
        Assert.False(e.FindProperty(nameof(DeviceEntity.Name))!.IsNullable);
        Assert.False(e.FindProperty(nameof(DeviceEntity.ProtocolName))!.IsNullable);
        Assert.False(e.FindProperty(nameof(DeviceEntity.Endpoint))!.IsNullable);
        Assert.False(e.FindProperty(nameof(DeviceEntity.Status))!.IsNullable);
    }

    [Fact]
    public void Point_ColumnConstraintsAndIndex()
    {
        var e = Entity<PointEntity>();
        Assert.Equal(200, e.FindProperty(nameof(PointEntity.Name))!.GetMaxLength());
        Assert.Equal(200, e.FindProperty(nameof(PointEntity.Address))!.GetMaxLength());
        Assert.Equal(50, e.FindProperty(nameof(PointEntity.DataType))!.GetMaxLength());
        Assert.Equal(50, e.FindProperty(nameof(PointEntity.Access))!.GetMaxLength());
        Assert.False(e.FindProperty(nameof(PointEntity.Name))!.IsNullable);
        Assert.False(e.FindProperty(nameof(PointEntity.Address))!.IsNullable);

        var index = Assert.Single(e.GetIndexes());
        Assert.Equal(nameof(PointEntity.DeviceId), Assert.Single(index.Properties).Name);
    }

    [Fact]
    public void DeviceToPointForeignKey_Cascades()
    {
        var fk = Assert.Single(Entity<PointEntity>().GetForeignKeys());
        Assert.Equal(typeof(DeviceEntity), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    [Fact]
    public void Alarm_ColumnNamesAreSnakeCase()
    {
        var e = Entity<AlarmEntity>();
        Assert.Equal("id", e.FindProperty(nameof(AlarmEntity.Id))!.GetColumnName());
        Assert.Equal("rule_id", e.FindProperty(nameof(AlarmEntity.RuleId))!.GetColumnName());
        Assert.Equal("device_id", e.FindProperty(nameof(AlarmEntity.DeviceId))!.GetColumnName());
        Assert.Equal("point_id", e.FindProperty(nameof(AlarmEntity.PointId))!.GetColumnName());
        Assert.Equal("trigger_value", e.FindProperty(nameof(AlarmEntity.TriggerValue))!.GetColumnName());
        Assert.Equal("threshold", e.FindProperty(nameof(AlarmEntity.Threshold))!.GetColumnName());
        Assert.Equal("severity", e.FindProperty(nameof(AlarmEntity.Severity))!.GetColumnName());
        Assert.Equal("message", e.FindProperty(nameof(AlarmEntity.Message))!.GetColumnName());
        Assert.Equal("state", e.FindProperty(nameof(AlarmEntity.State))!.GetColumnName());
        Assert.Equal("first_exceeded_at", e.FindProperty(nameof(AlarmEntity.FirstExceededAt))!.GetColumnName());
        Assert.Equal("occurred_at", e.FindProperty(nameof(AlarmEntity.OccurredAt))!.GetColumnName());
        Assert.Equal("acknowledged_at", e.FindProperty(nameof(AlarmEntity.AcknowledgedAt))!.GetColumnName());
        Assert.Equal("resolved_at", e.FindProperty(nameof(AlarmEntity.ResolvedAt))!.GetColumnName());
        Assert.Equal("site_id", e.FindProperty(nameof(AlarmEntity.SiteId))!.GetColumnName());
        Assert.Equal(20, e.FindProperty(nameof(AlarmEntity.Severity))!.GetMaxLength());
        Assert.Equal(20, e.FindProperty(nameof(AlarmEntity.State))!.GetMaxLength());
    }

    [Fact]
    public void AlarmRule_ColumnNamesAreSnakeCase()
    {
        var e = Entity<AlarmRuleEntity>();
        Assert.Equal("id", e.FindProperty(nameof(AlarmRuleEntity.Id))!.GetColumnName());
        Assert.Equal("device_id", e.FindProperty(nameof(AlarmRuleEntity.DeviceId))!.GetColumnName());
        Assert.Equal("point_id", e.FindProperty(nameof(AlarmRuleEntity.PointId))!.GetColumnName());
        Assert.Equal("operator", e.FindProperty(nameof(AlarmRuleEntity.Operator))!.GetColumnName());
        Assert.Equal("threshold", e.FindProperty(nameof(AlarmRuleEntity.Threshold))!.GetColumnName());
        Assert.Equal("threshold_upper", e.FindProperty(nameof(AlarmRuleEntity.ThresholdUpper))!.GetColumnName());
        Assert.Equal("duration_seconds", e.FindProperty(nameof(AlarmRuleEntity.DurationSeconds))!.GetColumnName());
        Assert.Equal("severity", e.FindProperty(nameof(AlarmRuleEntity.Severity))!.GetColumnName());
        Assert.Equal("message_template", e.FindProperty(nameof(AlarmRuleEntity.MessageTemplate))!.GetColumnName());
        Assert.Equal("enabled", e.FindProperty(nameof(AlarmRuleEntity.Enabled))!.GetColumnName());
        Assert.Equal(20, e.FindProperty(nameof(AlarmRuleEntity.Operator))!.GetMaxLength());
        Assert.Equal(20, e.FindProperty(nameof(AlarmRuleEntity.Severity))!.GetMaxLength());
    }
}
