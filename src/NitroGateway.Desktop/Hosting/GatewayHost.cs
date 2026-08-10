using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm;
using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.Forwarder;
using NitroGateway.Host;
using NitroGateway.Persistence;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Protocols;
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
        DesktopPathConfig.Apply(builder.Configuration);

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
        builder.Services.AddNitroForwarder(builder.Configuration.GetValue("Forwarder:IntervalMs", 5000));
        builder.Services.AddNitroCollection(builder.Configuration);
        builder.Services.AddNitroMqtt(builder.Configuration);

        // ── 桌面壳（EventBridge / UiDispatcher / ViewModels）──
        builder.Services.AddNitroDesktopShell();

        return new GatewayHost(builder.Build());
    }

    /// <summary>
    /// 启动：先跑 FluentMigrator 迁移（与中心同 schema，复用 M001~ 迁移），
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



