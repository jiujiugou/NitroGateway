using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Alarm.Repository;
using NitroGateway.Security.Audit;
using NitroGateway.Security.Auth;
using NitroGateway.Persistence.Security;
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
    public static IServiceCollection AddNitroSqlite(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetValue<string>("Persistence:ConnectionString")
            ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");

        // EF Core（Configuration）
        services.AddDbContext<NitroGatewayDbContext>(options =>
            options.UseSqlite(connectionString));

        // ADR-073 D5：OPC UA 连接凭据保护（AES-256-GCM + env 主密钥 OpcUa:CredentialKey）。
        // 无状态单例（仅持有密钥串）；无 OPC UA 凭据的部署无需配置密钥，使用路径才 fail-fast。
        services.AddSingleton<ICredentialProtector>(_ => new AesGcmCredentialProtector(configuration));
        services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.AddScoped<IPointRepository, SqlitePointRepository>();

        services.AddSingleton<IMeasurementStore>(_ => new SqliteMeasurementStore(connectionString));
        services.AddSingleton<IForwardBuffer>(sp => new SqliteForwardOutbox(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteForwardOutbox>>(),
            maxRetries: 5,
            maxPending: configuration.GetValue("Persistence:ForwardBufferMaxPending", 100_000)));

        // 宿主启动（迁移完成后）调用 IForwardMqttToggle.InitializeAsync 把持久值加载进内存，
        // DataDispatcher 采集热路径同步读 IsEnabled，不落库。
        services.AddSingleton<IAppMetaStore>(_ => new SqliteAppMetaStore(connectionString));
        services.AddSingleton<IForwardMqttToggle, SqliteForwardMqttToggle>();

        services.AddHostedService(sp => new MeasurementRetentionService(
            sp.GetRequiredService<IMeasurementStore>(),
            sp.GetRequiredService<ILogger<MeasurementRetentionService>>(),
            retentionDays: configuration.GetValue("Persistence:MeasurementRetentionDays", 30),
            interval: configuration.GetValue<TimeSpan?>("Persistence:MeasurementRetentionInterval") ?? TimeSpan.FromHours(24)));

        // WAL 维护：定期 checkpoint，防止 WAL 在持续写负载下无限增长拖垮全部读写。
        services.AddHostedService(sp => new SqliteMaintenanceService(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteMaintenanceService>>(),
            interval: configuration.GetValue<TimeSpan?>("Persistence:WalCheckpointInterval") ?? TimeSpan.FromSeconds(60)));

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
        // 热路径规则读取从"每设备每秒一次 DB 查询"降为"首次加载 + 写成功后重载"；
        // 写成功失效缓存保证立即一致，TTL（默认 30s）兜底进程外直改库/多实例场景。
        // 内层按具体类型注册（AddScoped<SqliteAlarmRuleRepository>()），装饰器按类型解析，避免循环依赖。
        services.AddScoped<SqliteAlarmRuleRepository>();
        services.AddSingleton<AlarmRuleCache>();
        services.AddScoped<IAlarmRuleRepository>(sp => new CachedAlarmRuleRepository(
            sp.GetRequiredService<AlarmRuleCache>(),
            sp.GetRequiredService<SqliteAlarmRuleRepository>()));
        services.AddScoped<IAlarmRepository, SqliteAlarmRepository>();

        // audit_logs，Webapi 审计查询页读取；同 MeasurementStore 模式，不依赖 DbContext。
        services.AddSingleton<IAuditLogStore>(sp => new SqliteAuditLogStore(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteAuditLogStore>>()));

        // 首启空表时由 Webapi 启动期 SeedIfEmptyAsync 灌入配置用户（保留 admin/admin123 开发登录）
        services.AddSingleton<IUserStore>(sp => new SqliteUserStore(
            connectionString,
            sp.GetRequiredService<ILogger<SqliteUserStore>>()));

        return services;
    }
}

