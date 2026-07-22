using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// Configuration 用 EF Core，TimeSeries、Buffer、Alarm 共用裸 SqliteConnection。
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

        // 裸 SqliteConnection — Singleton，所有非 EF Core 的存储共用
        var conn = new SqliteConnection(connectionString);
        services.AddSingleton<IMeasurementStore>(_ => new SqliteMeasurementStore(connectionString));
        services.AddSingleton<IForwardBuffer>(_ => new SqliteForwardBuffer(conn));

        // 告警持久化（替代 Alarm 模块的 InMemory 实现）
        services.AddSingleton<IAlarmRuleRepository>(_ => new SqliteAlarmRuleRepository(conn));
        services.AddSingleton<IAlarmRepository>(_ => new SqliteAlarmRepository(conn));

        return services;
    }
}
