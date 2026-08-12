using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop;

/// <summary>桌面壳 DI 注册（ADR-026）：EventBridge（服务事件 → UI 帧）+ UiDispatcher + 各页面 ViewModel。</summary>
public static class DesktopServiceCollectionExtensions
{
    /// <summary>
    /// 注册桌面 UI 数据通道与页面 ViewModel。
    /// EventBridge 同时实现 <see cref="IPointStoredSink"/>（采集数据）、
    /// <see cref="IDeviceHealthListener"/>（设备健康）与 <see cref="IMqttStateListener"/>（MQTT 状态），
    /// 由既有模块的注册机制自动接入。
    /// </summary>
    public static IServiceCollection AddNitroDesktopShell(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<UiDispatcher>();

        services.AddSingleton<EventBridge>();
        services.AddSingleton<IPointStoredSink>(sp => sp.GetRequiredService<EventBridge>());
        services.AddSingleton<IDeviceHealthListener>(sp => sp.GetRequiredService<EventBridge>());
        services.AddSingleton<IMqttStateListener>(sp => sp.GetRequiredService<EventBridge>());

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<RealtimeViewModel>();
        services.AddSingleton<AlarmsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // ADR-029 P4：设备/点位编辑对话框（WPF 模态实现；ViewModel 依赖接口便于单测）
        services.AddSingleton<IDeviceDialogService, DeviceDialogService>();

        // ADR-033 阶段 2：中心配置导入（地址/Token 本机存储 + 快照拉取 + 以中心为准重置本地）。
        // HttpClient 按 Singleton 注册并统一超时，避免每次导入新建连接资源。
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<ICenterSyncSettingsStore>(_ => new CenterSyncSettingsStore());
        services.AddSingleton<ICenterConfigClient, CenterConfigClient>();
        services.AddSingleton<ICenterConfigImporter, CenterConfigImporter>();

        // ADR-033 阶段 3/4：配置自动同步——outbox（现场待上报队列）与周期同步服务。
        // 同步服务与采集/转发/告警同为后台服务；未配置中心地址时静默跳过（手动导入模式）。
        services.AddSingleton<IConfigSyncOutboxStore>(_ =>
        {
            var connectionString = configuration["Persistence:ConnectionString"]
                ?? throw new InvalidOperationException("Persistence:ConnectionString 未配置。");
            return new ConfigSyncOutboxStore(connectionString);
        });
        services.AddHostedService<SiteConfigSyncService>();
        return services;
    }
}
