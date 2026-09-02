using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using NitroGateway.Alarm;
using NitroGateway.Collection;
using NitroGateway.Command;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;
using NitroGateway.Forwarder;
using NitroGateway.Host;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Protocols;
using NitroGateway.Security;
using NitroGateway.Security.Audit;
using NitroGateway.Security.Auth;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi;
using NitroGateway.Webapi.HealthChecks;
using NitroGateway.Webapi.Hubs;
using NitroGateway.Webapi.Services;
using Prometheus;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
// ── 参数 ──
// ADR-014：健康检查与 AddNitroSqlite 统一读取 Persistence:ConnectionString；
// 此前误读不存在的 Persistence:DbPath，导致 sqlite 健康检查拿到空连接串。
var dbConnectionString = builder.Configuration["Persistence:ConnectionString"]
    ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");

// ── Serilog ──

// ADR-014：Serilog 作为唯一日志输出（appsettings.json 已移除 Logging 段），
// 清掉宿主内置 Console/Debug 提供程序，避免与 Serilog Console sink 双写。
builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration)
                 .ReadFrom.Services(services)
                 .Enrich.FromLogContext();
});

// ── DI ──
// ── 安全 ──

builder.Services.AddNitroSecurity(builder.Configuration);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddNitroGatewayHost();
builder.Services.AddNitroSqlite(builder.Configuration);
builder.Services.AddNitroDevice();
builder.Services.AddNitroProtocol();
builder.Services.AddNitroSignalR();
// ADR-054：web 收敛为纯边缘（Linux 网关管理端，Gateway 单一形态），不再有 Center 模式。
// 采集/转发/MQTT 发布/告警评估无条件注册——中心（如需多现场）另立独立项目，不复用 webapi 双模式。
// ADR-016 P1-1：Forwarder 必须先于 Collection 注册——HostedService 按注册序反向停止，
// 这样关闭时先停采集（最后一轮入缓冲）再停转发（停机排空），MQTT 由 Singleton 兜底保持连接。
// ADR-022 P3-7：Forwarder 轮询间隔配置化（与 Collection 一致），缺省 5000ms
// ADR-011 P2：配置驱动注册（Forwarder:Channels 决定 MQTT/HTTP 引擎），缺省 mqtt 单通道
builder.Services.AddNitroAlarm();
builder.Services.AddNitroForwarder(builder.Configuration);
builder.Services.AddNitroCollection(builder.Configuration);
builder.Services.AddNitroMqtt(builder.Configuration);
// ADR-069：命令回写（云→网关→PLC，带回执）——订阅 commands topic，与转发共用同一 IMqttClient 单例。
builder.Services.AddNitroCommand();

// ── 站点标识 + 配置同步（ADR-036 / ADR-033 阶段 4：边缘→中心 push）──
// 站点 ID：配置/环境变量 Site:Id → app_meta 持久化 → 自动生成并持久化；Singleton 构造时解析。
// 首解析发生在 Program 启动（InitializeDatabase 建表后）写回配置，供采集/转发/告警/状态页统一取用。
builder.Services.AddSingleton<ISiteIdProvider, SiteIdProvider>();
// 中心配置客户端：独立 HttpClient（非 Forwarder 的 IHttpClient），统一 15s 超时，避免每次请求建连接。
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
builder.Services.AddSingleton<ICenterConfigClient, CenterConfigClient>();
// ADR-073 D8：OPC UA 服务器证书信任管理（操作 pki 目录，信任状态唯一权威，不入 SQLite）。
// pki 根目录默认相对进程工作目录（与 OpcUaDriver BuildConfiguration 目录 StorePath 同源）；
// 可用配置 OpcUa:PkiDirectory 覆盖（如把 pki 卷挂载到持久化目录）。
builder.Services.AddSingleton<IOpcUaCertificateManager>(sp =>
{
    var pkiRoot = builder.Configuration["OpcUa:PkiDirectory"]
        ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "opcua", "pki");
    return new OpcUaCertificateManager(
        pkiRoot,
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpcUaCertificateManager>>());
});
// outbox：边缘设备/点位增删改先入队，同步服务联网后按序上报中心；写入失败不阻断主操作。
builder.Services.AddSingleton<IConfigSyncOutboxStore>(_ => new ConfigSyncOutboxStore(dbConnectionString));
// 周期同步：拉中心快照双向 UpdatedAt 合并 + 上报 outbox；未配置 ConfigSync:CenterUrl 时静默跳过。
builder.Services.AddHostedService<WebConfigSyncService>();

// ADR-056：启用 OpenTelemetry 追踪（OTLP/Console 导出），service.name=nitrogateway-webapi；
// 配置见 appsettings Telemetry:Tracing，可用环境变量覆盖（Endpoint 空时走 OTEL_EXPORTER_OTLP_ENDPOINT）。
builder.Services.AddNitroTelemetry(builder.Configuration, "nitrogateway-webapi");

// ── 健康检查 ──
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("sqlite", new SqliteHealthCheck(dbConnectionString), tags: ["db", "ready"])
    .AddCheck<DiskHealthCheck>("disk", tags: ["disk"]);
// ADR-054：MQTT 健康检查依赖转发链路（IMqttClient），边缘形态恒注册，不再按 Center 跳过。
healthChecks.AddCheck<MqttHealthCheck>("mqtt", tags: ["mqtt", "ready"]);
// ADR-011 P4：HTTP 北向通道检查——仅 http/both 通道启用时注册（IHttpClient 只在此时存在）
var forwarderChannels = builder.Configuration["Forwarder:Channels"] ?? "mqtt";
if (forwarderChannels.Trim().Equals("http", StringComparison.OrdinalIgnoreCase) ||
    forwarderChannels.Trim().Equals("both", StringComparison.OrdinalIgnoreCase))
{
    healthChecks.AddCheck<HttpHealthCheck>("http", tags: ["http", "ready"]);
}

builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "NitroGateway API",
        Version = "v1",
        Description = "工业协议边缘网关 REST API — 设备管理、点位采集、告警、MQTT 转发"
    });

    // JWT Bearer 认证
    c.AddSecurityDefinition("Bearer", new()
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "输入 JWT token: Bearer {token}"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
{
    {
        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference
            {
                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                Id = "Bearer"
            }
        },
        Array.Empty<string>()
    }
});

    // XML 注释
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
});
var app = builder.Build();

// ── 建表 ──
app.InitializeDatabase();

// ADR-036：站点标识解析并写回配置——建表（app_meta 持久化可用）后首次 resolve；
// 解析结果写回 Configuration，使构造期读 Site:Id 的采集/转发/告警/StatusController 拿到真实值而非 default。
var siteId = app.Services.GetRequiredService<ISiteIdProvider>().Current;
app.Configuration["Site:Id"] = siteId;

// ADR-059：MQTT 转发总开关——迁移完成后把持久值（app_meta）加载进内存，
// 供 DataDispatcher 采集热路径与 ForwarderController 读取；缺省/失败按启用处理，不阻断启动。
await using (var toggleScope = app.Services.CreateAsyncScope())
{
    var toggle = toggleScope.ServiceProvider.GetRequiredService<IForwardMqttToggle>();
    await toggle.InitializeAsync();
}

// ADR-066：首启种子——users 表为空时从配置用户（Security:Users，已在 AddNitroSecurity 归一化为哈希）灌入，
// 保住 admin/admin123 开发登录；表非空则跳过（配置仅引导，运行时账号全部落库、由 UserController 管理）。
await using (var userScope = app.Services.CreateAsyncScope())
{
    var userStore = userScope.ServiceProvider.GetRequiredService<IUserStore>();
    var configUsers = userScope.ServiceProvider.GetRequiredService<IReadOnlyList<UserConfig>>();
    await userStore.SeedIfEmptyAsync(configUsers);
}

// ADR-022 P3-6：Swagger 仅开发环境暴露；生产 API 面不对外展示（本地开发由 launchSettings 驱动）
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
// ADR-004 P2-4：异常处理中间件注册在审计之后（内层），端点异常先转 500 再让审计记录真实状态码
app.UseMiddleware<AuditMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.MapHealthChecks("/healthz", new() { Predicate = _ => true });
app.MapHealthChecks("/readyz", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapMetrics();
app.MapControllers();
// ADR-022 P1-1：Hub 强制登录（JWT 经 query string access_token 校验），禁止匿名订阅
app.MapHub<LiveDataHub>("/hubs/live").RequireAuthorization();
app.Run();
