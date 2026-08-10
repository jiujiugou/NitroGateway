using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddNitroDesktopShell(this IServiceCollection services)
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
        return services;
    }
}
