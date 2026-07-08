using Microsoft.Extensions.Diagnostics.HealthChecks;
using NitroGateway.Alarm;
using NitroGateway.Collection;
using NitroGateway.Collection.Cache;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Host;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Protocol.Modbus;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;
using NitroGateway.Webapi.HealthChecks;
using NitroGateway.Webapi.Hubs;
using Prometheus;
using Serilog;
using NitroGateway.Webapi;

// ── 参数 ──
var dbPath = Path.GetFullPath("nitrogateway.db");
var remainingArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--db" && i + 1 < args.Length) dbPath = Path.GetFullPath(args[++i]);
    else remainingArgs.Add(args[i]);
}

// ── Serilog ──
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .WriteTo.File(
        new Serilog.Formatting.Compact.CompactJsonFormatter(),
        "logs/nitrogateway-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder([.. remainingArgs]);

    builder.Host.UseSerilog();

// ── DI ──
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddNitroGatewayHost();
builder.Services.AddNitroSqlite($"Data Source={dbPath}");
builder.Services.AddNitroDevice();
builder.Services.AddNitroModbus();
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

var app = builder.Build();

// ── 建表 ──
MigrationRunner.Run($"Data Source={dbPath}");

// ── DeviceCache 初始化（从 DB 全量加载到内存）──
{
    var cache = app.Services.GetRequiredService<DeviceCache>();
    var dm = app.Services.GetRequiredService<IDeviceManager>();
    await cache.LoadAsync(dm);
}

// ── HealthMonitor ↔ CircuitBreaker 联动 ──
// 设备恢复 Online → 强制重置熔断器为 Closed
{
    var monitor = app.Services.GetRequiredService<IDeviceHealthMonitor>();
    var breakers = app.Services.GetRequiredService<ICircuitBreakerRegistry>();
    monitor.ThresholdReached += (deviceId, status) =>
    {
        if (status == NitroGateway.Domain.Devices.DeviceStatus.Online)
        {
            breakers.Reset(deviceId);
        }
    };
}

// ── MQTT（后台）──
_ = Task.Run(async () =>
{
    var mqtt = app.Services.GetRequiredService<IMqttClient>();
    var r = await mqtt.ConnectAsync();
    if (r.IsSuccess) await mqtt.SubscribeAsync("nitrogateway/+/cmd");
});

app.UseCors();
app.MapHealthChecks("/healthz", new() { Predicate = _ => true });
app.MapHealthChecks("/readyz", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapMetrics();
app.MapControllers();
app.MapHub<LiveDataHub>("/hubs/live");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "应用启动失败");
}
finally
{
    Log.CloseAndFlush();
}
