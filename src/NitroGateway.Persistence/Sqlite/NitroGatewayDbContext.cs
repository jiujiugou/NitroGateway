using Microsoft.EntityFrameworkCore;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>EF Core 数据上下文，管理 Configuration 表（设备 + 点位）</summary>
public sealed class NitroGatewayDbContext : DbContext
{
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<PointEntity> Points => Set<PointEntity>();

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
    }
}
