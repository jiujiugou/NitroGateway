using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

/// <summary>
/// 数据转发实现：Dequeue → Serialize → MQTT Publish（QoS 1）→ Commit。
/// <para>关键设计：</para>
/// <list type="bullet">
/// <item>内嵌自适应节流（<see cref="ForwardingThrottle"/>，AIMD）：失败收紧、成功放松，防止 MQTT 恢复时冲垮 Broker；</item>
/// <item>失败批次 MarkFailed（重试计数 +1，超限自动进死信），隔离坏消息，不阻塞后续批次；</item>
/// <item>仅发布成功才 Commit 删除，保证至少一次语义；Commit/MarkFailed 失败显式记录 Error 日志，避免静默丢数；</item>
/// <item>每轮记录 Activity（<see cref="GatewayActivities.Forward"/>）与 Prometheus 指标（ForwardTotal / BufferBacklog / ThrottleBatchSize）。</item>
/// </list>
/// </summary>
public sealed class Forwarder : IForwarder
{
    /// <summary>转发缓冲：两阶段语义（Pending → InFlight → 删除），失败批次经重试计数超限进入死信</summary>
    private readonly IForwardBuffer _buffer;

    /// <summary>消息序列化器：BatchMeasurements → 发布负载字节</summary>
    private readonly IMessageSerializer _serializer;

    /// <summary>MQTT 客户端：QoS 1 发布，非成功返回码视为该批转发失败</summary>
    private readonly IMqttClient _mqtt;

    /// <summary>全局节流器（Singleton）：AIMD 调整单次出队批量与批次间延迟</summary>
    private readonly ForwardingThrottle _throttle;

    /// <summary>日志</summary>
    private readonly ILogger<Forwarder> _logger;

    /// <summary>创建转发器</summary>
    /// <param name="buffer">转发缓冲：负责 Pending/重试计数/死信状态管理</param>
    /// <param name="serializer">消息序列化器：BatchMeasurements → 发布负载字节</param>
    /// <param name="mqtt">MQTT 客户端：发布失败（非成功返回码）即视为该批转发失败</param>
    /// <param name="throttle">全局节流器（Singleton），跨调度周期保持 AIMD 状态</param>
    /// <param name="logger">日志</param>
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
    /// <remarks>
    /// 单批转发失败不会导致整体失败：失败批次已 MarkFailed（重试/死信），方法仍返回 Success，
    /// 调用方无需感知个别批次结果；仅 Dequeue 失败返回 Failure，此时缓冲原状保留、下轮重试。
    /// Activity 状态（ADR-001 P2-9）：全成功置 Ok，任一失败/异常/提交失败置 Error。
    /// </remarks>
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
            // ADR-017 P2-1：空轮也必须刷新指标，否则积压清空后 BufferBacklog 恒显旧值
            NitroMetrics.BufferBacklog.Set(0);
            NitroMetrics.ThrottleBatchSize.Set(_throttle.MaxBatchSize);
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
            catch (OperationCanceledException)
            {
                // ADR-017 P2-2：取消不是转发失败——不做 MarkFailed / failure 计数 / 节流收紧，
                // 上抛让引擎按停机路径处理（正常停机会排空剩余 Pending）；已出队未处理批次
                // 保持 InFlight，由下次启动恢复兜底（ADR-001 P0-1①）。
                activity?.SetStatus(ActivityStatusCode.Error, "转发轮被取消");
                throw;
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

        if (committed.Count > 0 && !ct.IsCancellationRequested)
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

        // ADR-017 P3-1：改走异步 GetCountAsync，不再每轮同步查库（ADR-001 P3-13 约定）
        NitroMetrics.BufferBacklog.Set(await _buffer.GetCountAsync(ct));
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
    /// <param name="batchId">失败的批次 ID</param>
    /// <param name="reason">失败原因，写入缓冲供排查</param>
    /// <param name="ct">取消令牌，透传给缓冲</param>
    private async Task MarkFailedOrLogErrorAsync(Guid batchId, string reason, CancellationToken ct)
    {
        var markResult = await _buffer.MarkFailedAsync(batchId, reason, ct);
        if (markResult.IsFailure)
        {
            _logger.LogError("标记批次 {BatchId} 失败（批次将卡 InFlight）: {Error}", batchId, markResult.Error!.Message);
        }
    }
}
