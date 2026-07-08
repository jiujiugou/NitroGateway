using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Alarm.Evaluation;
using NitroGateway.Alarm.Hosted;
using NitroGateway.Alarm.Notification;
using NitroGateway.Domain.Events;

namespace NitroGateway.Alarm;

/// <summary>Alarm 模块 DI 注册</summary>
public static class AlarmServiceCollectionExtensions
{
    /// <summary>注册告警子系统</summary>
    public static IServiceCollection AddNitroAlarm(this IServiceCollection services)
    {
        // 仓储（占位内存实现，后续替换为 Persistence 层实现）
        services.AddSingleton<Repository.IAlarmRuleRepository, Repository.InMemoryAlarmRuleRepository>();
        services.AddSingleton<Repository.IAlarmRepository, Repository.InMemoryAlarmRepository>();

        // AlarmHostedService 同时作为 BackgroundService 和 IPointStoredSink
        services.AddSingleton<AlarmHostedService>();
        services.AddSingleton<IPointStoredSink>(sp => sp.GetRequiredService<AlarmHostedService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmHostedService>());

        // Evaluator（Singleton，维护 Duration 状态）
        services.AddSingleton<AlarmEvaluator>();

        // 通知渠道（可按需增加）
        services.AddSingleton<IAlarmNotifier, MqttAlarmNotifier>();

        return services;
    }
}
