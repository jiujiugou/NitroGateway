using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

/// <summary>
/// 数据转发实现。
/// Dequeue → Serialize → MQTT Publish → Commit。
/// 内嵌自适应节流 + 死信队列，防止 MQTT 恢复时冲垮 Broker 并隔离坏消息。
/// </summary>
public sealed class Forwarder : IForwarder
{
    private readonly IForwardBuffer _buffer;
    private readonly IMessageSerializer _serializer;
    private readonly IMqttClient _mqtt;
    private readonly ForwardingThrottle _throttle;
    private readonly ILogger<Forwarder> _logger;

    /// <summary>创建转发器</summary>
    public Forwarder(
        IForwardBuffer buffer,
        IMessageSerializer serializer,
        IMqttClient mqtt,
        ForwardingThrottle throttle,
        ILogger<Forwarder> logger)
    {
        _buffer = buffer;
        _serializer = serializer;
        _mqtt = mqtt;
        _throttle = throttle;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Forward);

        // ── 节流限制单次出队量 ──
        var takeCount = Math.Min(maxCount, _throttle.MaxBatchSize);

        var dequeueResult = await _buffer.DequeueAsync(takeCount, ct);
        if (dequeueResult.IsFailure || dequeueResult.Value!.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }

        activity?.SetTag(GatewayActivityTags.BatchSize, dequeueResult.Value!.Count);

        var committed = new List<Guid>();

        foreach (var batch in dequeueResult.Value!)
        {
            // ── 批次间延迟（节流生效时）──
            await _throttle.ApplyDelayAsync(ct);

            try
            {
                var payload = _serializer.Serialize(batch);
                var topic = $"nitrogateway/{batch.DeviceId}/measurements";
                var result = await _mqtt.PublishAsync(topic, payload, qos: 1, ct);

                if (result.IsSuccess)
                {
                    committed.Add(batch.Id);
                    _throttle.OnMqttSuccess();
                    NitroMetrics.ForwardTotal.WithLabels("success").Inc();
                }
                else
                {
                    _logger.LogWarning("转发失败 {BatchId}: {Error}", batch.Id, result.Error!.Message);
                    _throttle.OnMqttFailure();
                    await _buffer.MarkFailedAsync(batch.Id, result.Error!.Message, ct);
                    NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发异常 {BatchId}", batch.Id);
                _throttle.OnMqttFailure();
                await _buffer.MarkFailedAsync(batch.Id, ex.Message, ct);
                NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
            }
        }

        if (committed.Count > 0)
            await _buffer.CommitAsync(committed, ct);

        NitroMetrics.BufferBacklog.Set(_buffer.Count);
        NitroMetrics.ThrottleBatchSize.Set(_throttle.MaxBatchSize);

        activity?.SetStatus(ActivityStatusCode.Ok);
        return OperationResult.Success();
    }
}
