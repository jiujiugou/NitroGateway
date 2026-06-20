using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Configuration;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Infrastructure.Sqlite;

/// <summary>SQLite 存储 DI 注册</summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// 注册全部 SQLite 存储服务。
    /// Configuration 用 EF Core，TimeSeries 和 Buffer 用裸 SqliteConnection。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="connectionString">SQLite 连接字符串，如 "Data Source=nitrogateway.db"</param>
    public static IServiceCollection AddNitroSqlite(
        this IServiceCollection services, string connectionString)
    {
        // EF Core（Configuration）
        services.AddDbContext<NitroGatewayDbContext>(options =>
            options.UseSqlite(connectionString));

        // EF Core 的 DbContext 是 Scoped，Repository 也必须 Scoped 才能共享生命周期
        services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.AddScoped<IPointRepository, SqlitePointRepository>();

        // 手工 SqliteConnection（TimeSeries + Buffer）— Singleton，一直开着
        var conn = new SqliteConnection(connectionString);
        services.AddSingleton<IMeasurementStore>(_ => new SqliteMeasurementStore(conn));
        services.AddSingleton<IForwardBuffer>(_ => new SqliteForwardBuffer(conn));

        return services;
    }
}
