using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Domain;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Alarm.Notification;

/// <summary>通过 MQTT 推送告警通知</summary>
public sealed class MqttAlarmNotifier : IAlarmNotifier
{
    private readonly IMqttClient _mqtt;
    private readonly ILogger<MqttAlarmNotifier> _logger;
    /// <summary>站点标识（ADR-035 第 1 步）：告警上行 topic 第三层 nitrogateway/{siteId}/{deviceId}/alarms</summary>
    private readonly string _siteId;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public string Name => "MQTT";

    public MqttAlarmNotifier(IMqttClient mqtt, ILogger<MqttAlarmNotifier> logger, string? siteId = null)
    {
        _mqtt = mqtt;
        _logger = logger;
        _siteId = string.IsNullOrWhiteSpace(siteId) ? SiteOptions.DefaultSiteId : siteId.Trim();
    }

    /// <inheritdoc />
    public async Task NotifyAsync(Domain.Alarm alarm, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            alarmId = alarm.Id.ToString(),
            alarm.RuleId,
            alarm.DeviceId,
            alarm.PointId,
            alarm.TriggerValue,
            alarm.Threshold,
            Severity = alarm.Severity.ToString(),
            alarm.Message,
            State = alarm.State.ToString(),
            alarm.OccurredAt
        }, _jsonOptions);

        // ADR-035 第 1 步：告警上行 topic 与遥测一致带站点层，中心 Ingest 按第三段解析 siteId
        var topic = $"nitrogateway/{_siteId}/{alarm.DeviceId}/alarms";
        var result = await _mqtt.PublishAsync(topic, Encoding.UTF8.GetBytes(payload), qos: 1, ct);

        if (result.IsSuccess)
            _logger.LogInformation("告警已推送 MQTT: {AlarmId} {Severity}", alarm.Id, alarm.Severity);
        else
            _logger.LogWarning("告警推送 MQTT 失败: {Error}", result.Error!.Message);
    }
}
