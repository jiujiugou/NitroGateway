using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm;
using NitroGateway.Collection;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Settings;
using NitroGateway.Desktop.Services.Sync;

using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Host;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Protocols;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using Serilog;

namespace NitroGateway.Desktop.Hosting;

/// <summary>
/// 桌面端宿主（ADR-026 D1/D3）：Host 构建 + 模块注册 + 启动迁移 + 优雅关闭。
/// 模块注册顺序与 Webapi 一致（Forwarder 先于 Collection，保证关闭时先停采集、再排空转发缓冲）。
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly IHost _host;

    /// <summary>DI 容器，View/ViewModel 的解析入口。</summary>
    public IServiceProvider Services => _host.Services;

    private GatewayHost(IHost host) => _host = host;

    /// <summary>
    /// 构建宿主：配置路径默认值 → Serilog → 模块注册 → 桌面壳。
    /// </summary>
    public static GatewayHost Create(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            // WPF 启动目录不固定，内容根固定为 exe 目录以稳定读取 appsettings.json
            ContentRootPath = AppContext.BaseDirectory
        });

        // D4 配置与路径：SQLite/日志缺省落 %LocalAppData%\NitroGateway，环境变量可覆盖
        // 设置页自定义日志目录（desktop-settings.json）在 Apply 内生效，重启后写入新位置
        DesktopPathConfig.Apply(builder.Configuration, settingsStore: new DesktopSettingsStore());

        // ADR-036 站点标识：配置/环境变量 > 本地存储 > 自动生成并持久化；
        // 解析结果写回配置，Forwarder/AlarmNotifier/ConfigSync/Settings 统一取用（缺省不再全叫 "default"）
        var siteStore = new SiteSettingsStore();
        builder.Configuration["Site:Id"] = SiteIdProvider.Resolve(builder.Configuration, siteStore);

        // ADR-067：MQTT 连接参数（设置页保存，desktop-settings.json）启动覆盖 appsettings；
        // 环境变量 MQTT__* 仍优先。须在 AddNitroMqtt 绑定 Options 前执行（下方模块注册时）。
        MqttDesktopConfig.Apply(builder.Configuration);

        // Serilog 作为唯一日志输出（与 Webapi 一致：清宿主提供程序 + 读配置 + DI 服务）
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());

        // ── 模块注册（顺序对齐 Webapi Program.cs；ADR-016 P1-1 关闭顺序）──
        builder.Services.AddNitroGatewayHost();
        builder.Services.AddNitroSqlite(builder.Configuration);
        builder.Services.AddNitroDevice();
        builder.Services.AddNitroProtocol();
        builder.Services.AddNitroAlarm();
        builder.Services.AddNitroForwarder(builder.Configuration);
        builder.Services.AddNitroCollection(builder.Configuration);
        builder.Services.AddNitroMqtt(builder.Configuration);

        // ── 桌面壳（EventBridge / UiDispatcher / ViewModels）──
        builder.Services.AddNitroDesktopShell(builder.Configuration);

        return new GatewayHost(builder.Build());
    }

    /// <summary>
    /// 启动：先跑 FluentMigrator 迁移（与 Webapi 同 schema，复用 M001~ 迁移），
    /// 再启动宿主与全部后台服务（采集 / 落库 / 转发 / 告警 / MQTT）。
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var configuration = _host.Services.GetRequiredService<IConfiguration>();
        var connectionString = configuration["Persistence:ConnectionString"]
            ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");

        var logger = _host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NitroGateway.Persistence.MigrationRunner");
        MigrationRunner.Run(connectionString, logger);

        // ADR-059：MQTT 转发总开关——迁移完成后把持久值（desktop-settings.json）加载进内存，
        // 供 DataDispatcher 采集热路径与设置页读取；缺省/失败按启用处理，不阻断启动。
        var toggle = _host.Services.GetRequiredService<IForwardMqttToggle>();
        await toggle.InitializeAsync(ct);

        await _host.StartAsync(ct);
    }

    /// <summary>
    /// 优雅关闭（D3）：IHost.StopAsync 按注册逆序停止——先采集（GatewayLifecycle drain，
    /// 最后一轮入缓冲）→ 转发排空 forward_buffer → MQTT 关闭；未 flush 完的数据留缓冲，下次启动续传。
    /// </summary>
    public Task StopAsync(CancellationToken ct = default) => _host.StopAsync(ct);

    public ValueTask DisposeAsync()
    {
        // IHost 只声明 IDisposable；异步释放需按 IAsyncDisposable 解包（Host 实际实现）
        return _host is IAsyncDisposable asyncDisposable
            ? asyncDisposable.DisposeAsync()
            : ValueTask.CompletedTask;
    }
}



