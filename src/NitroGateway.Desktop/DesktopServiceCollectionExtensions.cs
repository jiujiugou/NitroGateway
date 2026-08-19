using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Settings;
using NitroGateway.Desktop.Services.Sync;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;
using NitroGateway.Storage.Buffer;
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
        services.AddSingleton<AlarmRulesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // ADR-029 P4：设备/点位编辑对话框（WPF 模态实现；ViewModel 依赖接口便于单测）
        services.AddSingleton<IDeviceDialogService, DeviceDialogService>();

        // ADR-029 P2：点位 ViewModel 工厂（scope 解析收敛，对话框不再手工 new + GetRequiredService）
        services.AddSingleton<IPointsViewModelFactory, PointsViewModelFactory>();

        // 点位 CSV 导入/导出文件对话框（WPF 实现；ViewModel 依赖接口便于单测）
        services.AddSingleton<ICsvFileService, CsvFileService>();

        // ADR-044：桌面端连接测试（Connect+Ping，复用协议驱动工厂），供设备编辑窗口「测试连接」按钮
        services.AddSingleton<IDeviceConnectionTester, DeviceConnectionTester>();

        // ADR-043：告警规则编辑对话框（WPF 模态实现；ViewModel 依赖接口便于单测）
        services.AddSingleton<IAlarmRuleDialogService, AlarmRuleDialogService>();

        // ADR-033 阶段 2：中心配置导入（地址/Token 本机存储 + 快照拉取 + 以中心为准重置本地）。
        // HttpClient 按 Singleton 注册并统一超时，避免每次导入新建连接资源。
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<ICenterSyncSettingsStore>(_ => new CenterSyncSettingsStore());

        // 桌面端本地设置：日志目录（设置页可改，保存后重启生效；环境变量仍优先）
        services.AddSingleton<IDesktopSettingsStore>(_ => new DesktopSettingsStore());

        // ADR-059：MQTT 转发总开关——desktop-settings.json 持久化（重启保持）；
        // 宿主启动（迁移完成后）调用 IForwardMqttToggle.InitializeAsync 加载持久值到内存
        services.AddSingleton<IForwardMqttToggle, DesktopForwardMqttToggle>();

        // ADR-036 站点标识：site.json 存储 + 提供者（设置页展示/编辑/重新生成）
        services.AddSingleton<ISiteSettingsStore>(_ => new SiteSettingsStore());
        services.AddSingleton<ISiteIdProvider>(sp => new SiteIdProvider(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ISiteSettingsStore>()));
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
