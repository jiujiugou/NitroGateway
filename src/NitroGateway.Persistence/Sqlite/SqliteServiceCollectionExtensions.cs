using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Repository;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Configuration;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 存储 DI 注册</summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// 注册全部 SQLite 存储服务。
    /// Configuration 用 EF Core；TimeSeries、Buffer、Alarm 均按操作创建独立连接
    /// （ADR-001 P1-4：共享 Singleton 裸连接跨线程并发不安全）。
    /// </summary>
    public static IServiceCollection AddNitroSqlite(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetValue<string>("Persistence:ConnectionString")
            ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");

        // EF Core（Configuration）
        services.AddDbContext<NitroGatewayDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.AddScoped<IPointRepository, SqlitePointRepository>();

        services.AddSingleton<IMeasurementStore>(_ => new SqliteMeasurementStore(connectionString));
        services.AddSingleton<IForwardBuffer>(sp => new SqliteForwardBuffer(connectionString, sp.GetRequiredService<ILogger<SqliteForwardBuffer>>()));

        // ADR-002 P1-2：measurements 保留任务（后台周期清理，防止时序表无限增长）
        services.AddHostedService(sp => new MeasurementRetentionService(
            sp.GetRequiredService<IMeasurementStore>(),
            sp.GetRequiredService<ILogger<MeasurementRetentionService>>(),
            retentionDays: configuration.GetValue("Persistence:MeasurementRetentionDays", 30),
            interval: configuration.GetValue<TimeSpan?>("Persistence:MeasurementRetentionInterval") ?? TimeSpan.FromHours(24)));

        // 告警持久化（EF Core，Scoped 适配 DbContext；AlarmHostedService 每事件建 scope 解析）
        services.AddScoped<IAlarmRuleRepository, SqliteAlarmRuleRepository>();
        services.AddScoped<IAlarmRepository, SqliteAlarmRepository>();

        return services;
    }
}
