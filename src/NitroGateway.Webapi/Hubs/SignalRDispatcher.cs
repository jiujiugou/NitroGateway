using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;

namespace NitroGateway.Webapi.Hubs;

/// <summary>
/// SignalR 统一出口。所有对外推送写入内部 Channel，由 OutboxConsumer 在单一线程消费后发送。
/// </summary>
public class SignalRDispatcher : IPointStoredSink, IDeviceHealthListener
{
    private readonly ChannelWriter<OutboxMessage> _writer;
    private readonly ILogger<SignalRDispatcher> _logger;

    public SignalRDispatcher(Channel<OutboxMessage> channel, ILogger<SignalRDispatcher> logger)
    {
        _writer = channel.Writer;
        _logger = logger;
    }

    public ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
    {
        _logger.LogDebug("OnStoredAsync: Device={DeviceId}, Count={Count}", e.DeviceId, e.Snapshots.Count);
        var payload = e.Snapshots.Select(s => new OutboxMeasurement
        {
            DevicePointId = s.DevicePointId.ToString(),
            DeviceId = s.DeviceId.ToString(),
            Value = s.Value,
            Quality = s.Quality.ToString(),
            Timestamp = s.Timestamp.ToString("O")
        }).ToList();

        if (!_writer.TryWrite(new OutboxMessage
        {
            Method = "Measurement",
            TargetType = OutboxTarget.Group,
            GroupId = e.DeviceId.ToString(),
            Payload = payload
        }))
        {
            _logger.LogWarning("SignalR Channel 已满，丢弃 Measurement: Device={DeviceId}", e.DeviceId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        if (!_writer.TryWrite(new OutboxMessage
        {
            Method = "DeviceStatusChanged",
            TargetType = OutboxTarget.All,
            Payload = new { deviceId = e.DeviceId.ToString(), status = e.NewStatus.ToString() }
        }))
        {
            _logger.LogWarning("SignalR Channel 已满，丢弃 StatusChanged: Device={DeviceId}", e.DeviceId);
        }
        return ValueTask.CompletedTask;
    }
}

// ═══════════ Outbox 消息类型 ═══════════

public enum OutboxTarget { All, Group }

public sealed class OutboxMessage
{
    public required string Method { get; init; }
    public OutboxTarget TargetType { get; init; }
    public string GroupId { get; init; } = "";
    public required object Payload { get; init; }
}

public sealed record OutboxMeasurement
{
    public string DevicePointId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public object? Value { get; init; }
    public string Quality { get; init; } = "";
    public string Timestamp { get; init; } = "";
}
