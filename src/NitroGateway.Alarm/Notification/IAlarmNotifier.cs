using NitroGateway.Alarm.Domain;

namespace NitroGateway.Alarm.Notification;

/// <summary>
/// 告警通知接口。实现类负责将告警推送到特定渠道（MQTT、SignalR、邮件、钉钉等）。
/// AlarmHostedService 遍历所有已注册的 IAlarmNotifier 逐一通知。
/// </summary>
public interface IAlarmNotifier
{
    /// <summary>通知名称（用于日志标识）</summary>
    string Name { get; }

    /// <summary>发送告警通知</summary>
    Task NotifyAsync(Domain.Alarm alarm, CancellationToken ct = default);
}
