using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using NitroGateway.Alarm;
using NitroGateway.Collection;
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
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi;
using NitroGateway.Webapi.Deployment;
using NitroGateway.Webapi.HealthChecks;
using NitroGateway.Webapi.Hubs;
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
// ADR-033 阶段 3/4：配置同步接收（现场离线改动上报，UpdatedAt 合并 + tombstone 拒绝复活）
builder.Services.AddScoped<NitroGateway.Webapi.Services.ConfigSyncService>();
// ADR-035 第 0 步：按部署形态裁剪模块——Center（平台管理）不注册采集/转发/MQTT 发布，
// 中心库数据写点唯一为 Ingest；Gateway（边缘网关，默认）保持原有全量行为。
// 告警评估 + MQTT 通知（MqttAlarmNotifier 依赖 IMqttClient）属于边缘职责，Center 一并跳过；
// 中心侧告警规则 CRUD / 告警查询由 AddNitroSqlite 的仓储支撑，不受影响。
var deploymentMode = DeploymentModeParser.Parse(builder.Configuration);
var isCenter = deploymentMode == DeploymentMode.Center;
// ADR-044：部署形态注册为单例，供控制器按 Gateway/Center 裁剪边缘能力（test-connection/串口/转发状态）。
// B 阶段中心「意图下发」沿用同一 mode 语义，避免每处重复解析配置。
builder.Services.AddSingleton(typeof(DeploymentMode), deploymentMode);
if (!isCenter)
{
    builder.Services.AddNitroAlarm();
    // ADR-016 P1-1：Forwarder 必须先于 Collection 注册——HostedService 按注册序反向停止，
    // 这样关闭时先停采集（最后一轮入缓冲）再停转发（停机排空），MQTT 由 Singleton 兜底保持连接。
    // ADR-022 P3-7：Forwarder 轮询间隔配置化（与 Collection 一致），缺省 5000ms
    // ADR-011 P2：配置驱动注册（Forwarder:Channels 决定 MQTT/HTTP 引擎），缺省 mqtt 单通道
    builder.Services.AddNitroForwarder(builder.Configuration);
    builder.Services.AddNitroCollection(builder.Configuration);
    builder.Services.AddNitroMqtt(builder.Configuration);
}

builder.Services.AddNitroTelemetry();

// ── 健康检查 ──
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("sqlite", new SqliteHealthCheck(dbConnectionString), tags: ["db", "ready"])
    .AddCheck<DiskHealthCheck>("disk", tags: ["disk"]);
if (!isCenter)
{
    // MQTT/HTTP 健康检查依赖转发链路（IMqttClient/IHttpClient），Center 模式未注册，跳过
    healthChecks.AddCheck<MqttHealthCheck>("mqtt", tags: ["mqtt", "ready"]);
    // ADR-011 P4：HTTP 北向通道检查——仅 http/both 通道启用时注册（IHttpClient 只在此时存在）
    var forwarderChannels = builder.Configuration["Forwarder:Channels"] ?? "mqtt";
    if (forwarderChannels.Trim().Equals("http", StringComparison.OrdinalIgnoreCase) ||
        forwarderChannels.Trim().Equals("both", StringComparison.OrdinalIgnoreCase))
    {
        healthChecks.AddCheck<HttpHealthCheck>("http", tags: ["http", "ready"]);
    }
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
        Description = "工业协议边缘网关 REST API — 设备管理、点位采集、告警、死信"
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
