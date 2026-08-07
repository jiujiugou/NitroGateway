using Microsoft.EntityFrameworkCore;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>EF Core 数据上下文，管理 Configuration 表（设备 + 点位）</summary>
public sealed class NitroGatewayDbContext : DbContext
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<PointEntity> Points => Set<PointEntity>();
    public DbSet<AlarmEntity> Alarms => Set<AlarmEntity>();
    public DbSet<AlarmRuleEntity> AlarmRules => Set<AlarmRuleEntity>();

    public NitroGatewayDbContext(DbContextOptions<NitroGatewayDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DeviceEntity>(d =>
        {
            d.ToTable("devices");
            d.HasKey(x => x.Id);
            d.Property(x => x.Name).IsRequired().HasMaxLength(200);
            d.Property(x => x.ProtocolName).IsRequired().HasMaxLength(100);
            d.Property(x => x.ProtocolDialect).HasMaxLength(100);
            d.Property(x => x.Endpoint).IsRequired().HasMaxLength(500);
            d.Property(x => x.Status).IsRequired().HasMaxLength(50);
            d.HasMany(x => x.Points)
             .WithOne(p => p.Device)
             .HasForeignKey(p => p.DeviceId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PointEntity>(p =>
        {
            p.ToTable("points");
            p.HasKey(x => x.Id);
            p.Property(x => x.Name).IsRequired().HasMaxLength(200);
            p.Property(x => x.Address).IsRequired().HasMaxLength(200);
            p.Property(x => x.DataType).IsRequired().HasMaxLength(50);
            p.Property(x => x.Access).IsRequired().HasMaxLength(50);
            p.HasIndex(x => x.DeviceId);
        });

        // 告警表（M005 迁移建表，snake_case 列名）
        model.Entity<AlarmEntity>(a =>
        {
            a.ToTable("alarms");
            a.HasKey(x => x.Id);
            a.Property(x => x.Id).HasColumnName("id");
            a.Property(x => x.RuleId).HasColumnName("rule_id").IsRequired();
            a.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
            a.Property(x => x.PointId).HasColumnName("point_id").IsRequired();
            a.Property(x => x.TriggerValue).HasColumnName("trigger_value");
            a.Property(x => x.Threshold).HasColumnName("threshold");
            a.Property(x => x.Severity).HasColumnName("severity").IsRequired().HasMaxLength(20);
            a.Property(x => x.Message).HasColumnName("message");
            a.Property(x => x.State).HasColumnName("state").IsRequired().HasMaxLength(20);
            a.Property(x => x.FirstExceededAt).HasColumnName("first_exceeded_at");
            a.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
            a.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
            a.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        });

        model.Entity<AlarmRuleEntity>(r =>
        {
            r.ToTable("alarm_rules");
            r.HasKey(x => x.Id);
            r.Property(x => x.Id).HasColumnName("id");
            r.Property(x => x.DeviceId).HasColumnName("device_id").IsRequired();
            r.Property(x => x.PointId).HasColumnName("point_id").IsRequired();
            r.Property(x => x.Operator).HasColumnName("operator").IsRequired().HasMaxLength(20);
            r.Property(x => x.Threshold).HasColumnName("threshold").IsRequired();
            r.Property(x => x.ThresholdUpper).HasColumnName("threshold_upper");
            r.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
            r.Property(x => x.Severity).HasColumnName("severity").IsRequired().HasMaxLength(20);
            r.Property(x => x.MessageTemplate).HasColumnName("message_template");
            r.Property(x => x.Enabled).HasColumnName("enabled");
        });
    }
}
