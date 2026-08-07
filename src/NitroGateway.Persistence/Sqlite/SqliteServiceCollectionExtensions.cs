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

        // 告警持久化（替代 Alarm 模块的 InMemory 实现）
        services.AddSingleton<IAlarmRuleRepository>(_ => new SqliteAlarmRuleRepository(connectionString));
        services.AddSingleton<IAlarmRepository>(_ => new SqliteAlarmRepository(connectionString));

        return services;
    }
}
