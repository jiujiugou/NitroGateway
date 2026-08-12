using Microsoft.EntityFrameworkCore;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// EF Core 数据上下文，管理配置类表：devices / points（PascalCase 列名）与
/// alarms / alarm_rules（snake_case 列名，M005 建表）。
/// 仅用于配置读写；时序（measurements）与转发缓冲（forward_buffer）走 Dapper 独立连接，不在此上下文内。
/// </summary>
public sealed class NitroGatewayDbContext : DbContext
{
    /// <summary>设备表（含点位导航集合，删除级联）</summary>
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    /// <summary>点位表</summary>
    public DbSet<PointEntity> Points => Set<PointEntity>();

    /// <summary>告警记录表（snake_case 列名）</summary>
    public DbSet<AlarmEntity> Alarms => Set<AlarmEntity>();

    /// <summary>告警规则表（snake_case 列名）</summary>
    public DbSet<AlarmRuleEntity> AlarmRules => Set<AlarmRuleEntity>();

    /// <summary>以指定选项创建上下文；选项由 DI 的 AddDbContext 注册（UseSqlite）</summary>
    public NitroGatewayDbContext(DbContextOptions<NitroGatewayDbContext> options) : base(options) { }

    /// <summary>
    /// 配置实体映射：表名、主键、必填/长度约束、外键级联与索引。
    /// devices/points 列名保持 M003 的 PascalCase 历史现状（见 M003 类注释）；
    /// alarms/alarm_rules 显式映射 snake_case 列名。
    /// </summary>
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
            d.Property(x => x.UpdatedAt);
            d.Property(x => x.IsDeleted);
            d.Property(x => x.SiteId);
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
            p.Property(x => x.UpdatedAt);
            p.Property(x => x.IsDeleted);
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
            a.Property(x => x.SiteId).HasColumnName("site_id");
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


