using NitroGateway.Ingest;
using NitroGateway.Ingest.HealthChecks;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Transport.MQTT;
using Prometheus;
using Serilog;

// ── 中心 Ingest（ADR-025 D1：独立项目 + 独立容器）──
// 职责: 订阅 `nitrogateway/+/measurements` 与 `nitrogateway/+/alarms`
//       → 遥测批量 INSERT OR IGNORE（D2 记录级幂等）→ 告警 UPSERT（状态迁移覆盖）
//       → /metrics 暴露 ingest_* 指标；/healthz /readyz 供运维探活。
var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["Persistence:ConnectionString"]
    ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");

// ── Serilog：唯一日志输出（与 Webapi 一致）──
builder.Logging.ClearProviders();
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration)
                 .ReadFrom.Services(services)
                 .Enrich.FromLogContext();
});

// ── DI ──
// AddNitroSqlite: 复用 M001~ 迁移建中心库 + measurements 保留清理（与现场端同 schema，D3）
builder.Services.AddNitroSqlite(builder.Configuration);
builder.Services.AddNitroMqtt(builder.Configuration);
builder.Services.AddSingleton<IIngestStore>(_ => new SqliteIngestStore(connectionString));
builder.Services.AddHostedService<IngestService>();

builder.Services.AddHealthChecks()
    .AddCheck("sqlite", new IngestSqliteHealthCheck(connectionString), tags: ["db", "ready"])
    .AddCheck<IngestMqttHealthCheck>("mqtt", tags: ["mqtt", "ready"]);

var app = builder.Build();

// ── 建表（迁移前自动备份，与 Webapi 同一入口）──
app.InitializeDatabase();

app.MapMetrics();
app.MapHealthChecks("/healthz", new() { Predicate = _ => true });
app.MapHealthChecks("/readyz", new() { Predicate = r => r.Tags.Contains("ready") });

app.Run();
