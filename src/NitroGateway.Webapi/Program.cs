using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
using NitroGateway.Webapi.HealthChecks;
using NitroGateway.Webapi.Hubs;
using Prometheus;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
// ── 参数 ──
var dbPath = builder.Configuration["Persistence:DbPath"];

// ── Serilog ──

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
builder.Services.AddNitroAlarm();
builder.Services.AddNitroCollection(intervalMs: 1000);
builder.Services.AddNitroForwarder(intervalMs: 5000);
builder.Services.AddNitroMqtt(new MqttConnectionOptions { Broker = "tcp://localhost:1883", ClientId = $"NitroGateway-{Environment.MachineName}" });

builder.Services.AddNitroTelemetry();

// ── 健康检查 ──
builder.Services.AddHealthChecks()
    .AddCheck("sqlite", new SqliteHealthCheck($"Data Source={dbPath}"), tags: ["db", "ready"])
    .AddCheck<MqttHealthCheck>("mqtt", tags: ["mqtt", "ready"]);

builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NitroGateway API", Version = "v1",
        Description = "工业协议边缘网关 REST API — 设备管理、点位采集、告警、死信" });

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
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
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

// ── MQTT（后台）──
_ = Task.Run(async () =>
{
    var mqtt = app.Services.GetRequiredService<IMqttClient>();
    var r = await mqtt.ConnectAsync();
    if (r.IsSuccess) await mqtt.SubscribeAsync("nitrogateway/+/cmd");
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuditMiddleware>();
app.MapHealthChecks("/healthz", new() { Predicate = _ => true });
app.MapHealthChecks("/readyz", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapMetrics();
app.MapControllers();
app.MapHub<LiveDataHub>("/hubs/live");
app.Run();


