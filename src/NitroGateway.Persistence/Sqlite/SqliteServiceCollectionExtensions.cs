using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Alarm.Repository;
using NitroGateway.Storage.Disk;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.Configuration;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// SQLite 存储 DI 注册入口。Webapi 启动时调用，按存储类别注册合适的生命周期：
/// EF 配置仓储 Scoped、Dapper 存储 Singleton（内部每操作独立连接）、保留清理后台任务 HostedService。
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// 注册全部 SQLite 存储服务。
    /// Configuration 用 EF Core；TimeSeries、Buffer、Alarm 均按操作创建独立连接
    /// （ADR-001 P1-4：共享 Singleton 裸连接跨线程并发不安全）。
    /// 连接串从配置项 <c>Persistence:ConnectionString</c> 读取，缺失时抛出
    /// <see cref="InvalidOperationException"/>（配置错误应快速失败）。
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
        // ADR-018 P2-3：缓冲入队上限 + 死信保留天数均可配置，防止 MQTT 长期离线/坏消息无限累积
        services.AddSingleton<IForwardBuffer>(sp => new SqliteForwardBuffer(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteForwardBuffer>>(),
            maxRetries: 5,
            maxPending: configuration.GetValue("Persistence:ForwardBufferMaxPending", 100_000)));

        // ADR-002 P1-2：measurements 保留任务（后台周期清理，防止时序表无限增长）
        services.AddHostedService(sp => new MeasurementRetentionService(
            sp.GetRequiredService<IMeasurementStore>(),
            sp.GetRequiredService<ILogger<MeasurementRetentionService>>(),
            retentionDays: configuration.GetValue("Persistence:MeasurementRetentionDays", 30),
            interval: configuration.GetValue<TimeSpan?>("Persistence:MeasurementRetentionInterval") ?? TimeSpan.FromHours(24)));

        // ADR-018 P2-3：死信保留任务（后台周期清理，防止死信表无限增长，与 measurements 保留对称）
        services.AddHostedService(sp => new DeadLetterRetentionService(
            sp.GetRequiredService<IForwardBuffer>(),
            sp.GetRequiredService<ILogger<DeadLetterRetentionService>>(),
            retentionDays: configuration.GetValue("Persistence:DeadLetterRetentionDays", 30),
            interval: configuration.GetValue<TimeSpan?>("Persistence:DeadLetterRetentionInterval") ?? TimeSpan.FromHours(24)));

        // ADR-012：磁盘守卫——同一实例同时是 IDiskStatus（供采集/转发/健康检查联动）与 HostedService
        services.AddOptions<DiskGuardOption>()
            .Bind(configuration.GetSection(DiskGuardOption.SectionName))
            .Validate(o => o.WarningFreeBytes > o.CriticalFreeBytes, "Disk:WarningFreeBytes 必须大于 Disk:CriticalFreeBytes")
            .Validate(o => o.RecoveryMarginPercent is >= 0 and <= 100, "Disk:RecoveryMarginPercent 必须在 0-100")
            .ValidateOnStart();
        services.AddSingleton<IDiskStatus>(sp => new DiskGuardService(
            connectionString,
            sp.GetRequiredService<IOptions<DiskGuardOption>>(),
            sp.GetRequiredService<ILogger<DiskGuardService>>()));
        services.AddHostedService(sp => (DiskGuardService)sp.GetRequiredService<IDiskStatus>());

        // 告警持久化（EF Core，Scoped 适配 DbContext；AlarmHostedService 每事件建 scope 解析）
        services.AddScoped<IAlarmRuleRepository, SqliteAlarmRuleRepository>();
        services.AddScoped<IAlarmRepository, SqliteAlarmRepository>();

        return services;
    }
}
