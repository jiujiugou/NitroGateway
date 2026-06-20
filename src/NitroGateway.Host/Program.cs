using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Infrastructure.Sqlite;
using NitroGateway.Infrastructure.Sqlite.Migrations;
using NitroGateway.Scheduler;
using NitroGateway.Transport.MQTT;
// top-level 文件隐式在全局命名空间，需要显式引用本项目的 namespace
using NitroGateway.Host;

var builder = Host.CreateApplicationBuilder(args);

// ---- 存储 ----
var dbPath = Path.GetFullPath(args.Length > 0 ? args[0] : "nitrogateway.db");
builder.Services.AddNitroSqlite($"Data Source={dbPath}");

// ---- 设备管理 ----
builder.Services.AddNitroDevice();

// ---- 调度器 ----
builder.Services.AddNitroScheduler();

// ---- 采集引擎 ----
builder.Services.AddNitroCollection(intervalMs: 1000);

// ---- 转发器 ----
// MQTT 连接参数（可通过环境变量或配置文件覆盖）
var mqttBroker = Environment.GetEnvironmentVariable("NITRO_MQTT_BROKER") ?? "tcp://localhost:1883";
builder.Services.AddNitroMqtt(new MqttConnectionOptions
{
    Broker = mqttBroker,
    ClientId = $"NitroGateway-{Environment.MachineName}"
});

builder.Services.AddNitroForwarder(intervalMs: 5000);

// ---- 日志 ----
builder.Logging.AddConsole();

var app = builder.Build();

// ---- 启动 ----
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host");
logger.LogInformation("NitroGateway 启动中...");
logger.LogInformation("数据库: {Path}", Path.GetFullPath(dbPath));
logger.LogInformation("MQTT Broker: {Broker}", mqttBroker);

// 确保数据库结构（EF Core 建表 + FluentMigrator 建表）
// 注意：需删除旧 .db 文件后重新运行
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NitroGatewayDbContext>();
    await db.Database.EnsureCreatedAsync();
    logger.LogInformation("EF Core 表已就绪");
}
MigrationRunner.Run($"Data Source={dbPath}"); // FluentMigrator: measurements + forward_buffer
logger.LogInformation("FluentMigrator 表已就绪");

// 连接 MQTT（后台异步，失败不阻塞启动）
_ = Task.Run(async () =>
{
    var mqtt = app.Services.GetRequiredService<IMqttClient>();
    var connectResult = await mqtt.ConnectAsync();
    if (connectResult.IsFailure)
        logger.LogWarning("MQTT 连接失败（网关继续运行）: {Error}", connectResult.Error!.Message);
    else
    {
        logger.LogInformation("MQTT 已连接");
        await mqtt.SubscribeAsync("nitrogateway/+/cmd");
    }
});

// 首次运行自动插入测试设备（DeviceManager 是 Scoped，需要用 scope）
using (var seedScope = app.Services.CreateScope())
{
    var deviceMgr = seedScope.ServiceProvider.GetRequiredService<IDeviceManager>();
    var pointMgr = seedScope.ServiceProvider.GetRequiredService<IPointManager>();
    await DatabaseSeeder.SeedIfEmpty(deviceMgr, pointMgr, logger);
}

// 启动模拟 Modbus 设备（替代 ModbusPal，值自动波动）
var fakeDevice = new FakeModbusDevice(port: 502, unitId: 1);
fakeDevice.AddSensor(offset: 0, count: 2, name: "Temperature (Float)", initValue: 60.0, min: 0, max: 150, noise: 0.4);
fakeDevice.AddSensor(offset: 2, count: 1, name: "Pressure (Int16)",   initValue: 200,  min: 0, max: 500, noise: 0.3);
fakeDevice.AddSensor(offset: 3, count: 2, name: "FlowRate (Int32)",   initValue: 50,   min: 0, max: 2000, noise: 1.0);
_ = fakeDevice.StartAsync();

// 启动调度器（阻塞主线程直到 Ctrl+C）
var scheduler = app.Services.GetRequiredService<IScheduler>();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    logger.LogInformation("收到停止信号，正在退出...");
};

logger.LogInformation("NitroGateway 已启动，按 Ctrl+C 停止");
await scheduler.RunAsync(cts.Token);

scheduler.Stop();
logger.LogInformation("NitroGateway 已停止");
