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
        // P1-3①：Dequeue 失败必须显式暴露——否则出队异常被吞掉，转发静默停滞，
        // 批次停留在 Pending 且无任何信号。失败时记录 Error 并返回失败结果。
        if (dequeueResult.IsFailure)
        {
            _logger.LogError("转发出队失败: {Error}", dequeueResult.Error!.Message);
            // ADR-001 P2-9：失败路径显式置 Error 状态，追踪不再恒为 Ok
            activity?.SetStatus(ActivityStatusCode.Error, dequeueResult.Error!.Message);
            return OperationResult.Failure(dequeueResult.Error);
        }

        if (dequeueResult.Value!.Count == 0)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            return OperationResult.Success();
        }

        activity?.SetTag(GatewayActivityTags.BatchSize, dequeueResult.Value!.Count);

        var committed = new List<Guid>();
        // 本轮是否出现过失败：失败路径置 Activity Error，只有全成功才置 Ok（ADR-001 P2-9）
        var anyFailure = false;

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
                    await MarkFailedOrLogErrorAsync(batch.Id, result.Error!.Message, ct);
                    NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                    anyFailure = true;
                    activity?.SetStatus(ActivityStatusCode.Error, result.Error!.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发异常 {BatchId}", batch.Id);
                _throttle.OnMqttFailure();
                await MarkFailedOrLogErrorAsync(batch.Id, ex.Message, ct);
                NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                activity?.SetTag(GatewayActivityTags.ErrorMessage, ex.ToString());
                anyFailure = true;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            }
        }

        if (committed.Count > 0)
        {
            // P1-3②：Commit 失败会让已转发批次卡在 InFlight（仅进程重启时恢复），
            // 必须记录 Error 级日志，避免静默丢数。
            var commitResult = await _buffer.CommitAsync(committed, ct);
            if (commitResult.IsFailure)
            {
                _logger.LogError("转发批次提交失败 {Count} 批: {Error}", committed.Count, commitResult.Error!.Message);
                anyFailure = true;
                activity?.SetStatus(ActivityStatusCode.Error, commitResult.Error!.Message);
            }
        }

        NitroMetrics.BufferBacklog.Set(_buffer.Count);
        NitroMetrics.ThrottleBatchSize.Set(_throttle.MaxBatchSize);

        // 成功路径才置 Ok；任一批次失败/异常/提交失败已在上方置 Error
        if (!anyFailure)
            activity?.SetStatus(ActivityStatusCode.Ok);
        return OperationResult.Success();
    }

    /// <summary>
    /// P1-3②：标记失败后必须检查结果——MarkFailed 失败会让批次卡在 InFlight
    /// （不参与 Count、不再出队、仅进程重启时恢复），属高影响故障，记录 Error 级日志。
    /// </summary>
    private async Task MarkFailedOrLogErrorAsync(Guid batchId, string reason, CancellationToken ct)
    {
        var markResult = await _buffer.MarkFailedAsync(batchId, reason, ct);
        if (markResult.IsFailure)
        {
            _logger.LogError("标记批次 {BatchId} 失败（批次将卡 InFlight）: {Error}", batchId, markResult.Error!.Message);
        }
    }
}
