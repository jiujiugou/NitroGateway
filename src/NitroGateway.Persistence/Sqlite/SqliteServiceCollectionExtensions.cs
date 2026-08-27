using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Alarm.Repository;
using NitroGateway.Security.Audit;
using NitroGateway.Security.Auth;
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
        // ADR-018 P2-3：缓冲入队上限可配置，防止 MQTT 长期离线无限累积
        services.AddSingleton<IForwardBuffer>(sp => new SqliteForwardOutbox(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteForwardOutbox>>(),
            maxRetries: 5,
            maxPending: configuration.GetValue("Persistence:ForwardBufferMaxPending", 100_000)));

        // ADR-059：MQTT 转发总开关——app_meta 键值持久化（不改库结构、不加迁移，M006 已建表）。
        // 宿主启动（迁移完成后）调用 IForwardMqttToggle.InitializeAsync 把持久值加载进内存，
        // DataDispatcher 采集热路径同步读 IsEnabled，不落库。
        services.AddSingleton<IAppMetaStore>(_ => new SqliteAppMetaStore(connectionString));
        services.AddSingleton<IForwardMqttToggle, SqliteForwardMqttToggle>();

        // ADR-002 P1-2：measurements 保留任务（后台周期清理，防止时序表无限增长）
        services.AddHostedService(sp => new MeasurementRetentionService(
            sp.GetRequiredService<IMeasurementStore>(),
            sp.GetRequiredService<ILogger<MeasurementRetentionService>>(),
            retentionDays: configuration.GetValue("Persistence:MeasurementRetentionDays", 30),
            interval: configuration.GetValue<TimeSpan?>("Persistence:MeasurementRetentionInterval") ?? TimeSpan.FromHours(24)));

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
        // ADR-032 P1-2：规则量小且只在进程内 API 变更——外层挂 CachedAlarmRuleRepository 内存缓存，
        // 热路径规则读取从"每设备每秒一次 DB 查询"降为"首次加载 + 写成功后重载"；
        // 写成功失效缓存保证立即一致，TTL（默认 30s）兜底进程外直改库/多实例场景。
        // 内层按具体类型注册（AddScoped<SqliteAlarmRuleRepository>()），装饰器按类型解析，避免循环依赖。
        services.AddScoped<SqliteAlarmRuleRepository>();
        services.AddSingleton<AlarmRuleCache>();
        services.AddScoped<IAlarmRuleRepository>(sp => new CachedAlarmRuleRepository(
            sp.GetRequiredService<AlarmRuleCache>(),
            sp.GetRequiredService<SqliteAlarmRuleRepository>()));
        services.AddScoped<IAlarmRepository, SqliteAlarmRepository>();

        // ADR-065 A3：操作审计落库（Dapper 单例，每操作独立连接）——AuditMiddleware 非 GET 写
        // audit_logs，Webapi 审计查询页读取；同 MeasurementStore 模式，不依赖 DbContext。
        services.AddSingleton<IAuditLogStore>(sp => new SqliteAuditLogStore(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteAuditLogStore>>()));

        // ADR-066：用户存储（Dapper 单例，每操作独立连接）——TokenGenerator 登录/UserController 管理共用；
        // 首启空表时由 Webapi 启动期 SeedIfEmptyAsync 灌入配置用户（保留 admin/admin123 开发登录）
        services.AddSingleton<IUserStore>(sp => new SqliteUserStore(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteUserStore>>()));

        return services;
    }
}

