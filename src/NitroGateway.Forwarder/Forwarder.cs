using System.Collections.Concurrent;
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
/// <item>固定批量上限（<see cref="MaxDequeuePerCall"/>）限制单次出队量，防止 MQTT 恢复时一次冲垮 Broker；
/// 普通遥测量级下无需 AIMD 自适应节流（简化，2026-08-22）；</item>
/// <item><b>有界并发发布</b>：单轮在途 QoS1 发布数受 <c>maxConcurrentPublishes</c> 限制（默认 8），
/// 吞吐 ≈ 并发数/RTT，避免串行发布被公网 RTT 卡死；并发不保证发送顺序（遥测带时间戳，允许乱序）；</item>
/// <item>失败批次 MarkFailed（重试计数 +1，超限自动进死信），隔离坏消息，不阻塞后续批次；</item>
/// <item>仅发布成功才 Commit 删除，保证至少一次语义；Commit/MarkFailed 失败显式记录 Error 日志，避免静默丢数；</item>
/// <item>每轮记录 Activity（<see cref="GatewayActivities.Forward"/>）与 Prometheus 指标（ForwardTotal / BufferBacklog）。</item>
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

    /// <summary>单次调用最大出队批次数：固定上限，替代原 AIMD 自适应节流（简化，2026-08-22）。</summary>
    private const int MaxDequeuePerCall = 1000;

    /// <summary>并发发布数下限；低于此值退化为串行</summary>
    private const int MinConcurrentPublishes = 1;

    /// <summary>并发发布数上限；防止配置错误导致无限并发</summary>
    private const int MaxConcurrentPublishesCap = 64;

    /// <summary>站点标识（ADR-035 第 1 步）：上行 topic 第三层 nitrogateway/{siteId}/{deviceId}/measurements</summary>
    private readonly string _siteId;

    /// <summary>单轮转发最大在途发布数（QoS1 ack 未回前的并发上限）；运行时夹紧到 [1, 64]。</summary>
    private readonly int _maxConcurrentPublishes;

    /// <summary>日志</summary>
    private readonly ILogger<Forwarder> _logger;

    /// <summary>创建转发器</summary>
    /// <param name="buffer">转发缓冲：负责 Pending/重试计数/死信状态管理</param>
    /// <param name="serializer">消息序列化器：BatchMeasurements → 发布负载字节</param>
    /// <param name="mqtt">MQTT 客户端：发布失败（非成功返回码）即视为该批转发失败</param>
    /// <param name="logger">日志</param>
    /// <param name="siteId">站点标识；缺省/空白回退 <see cref="SiteOptions.DefaultSiteId"/>（兼容既有构造与单现场部署）</param>
    /// <param name="maxConcurrentPublishes">单轮最大在途发布数；默认 8，夹紧到 [1, 64]</param>
    public Forwarder(
        IForwardBuffer buffer,
        IMessageSerializer serializer,
        IMqttClient mqtt,
        ILogger<Forwarder> logger,
        string? siteId = null,
        int maxConcurrentPublishes = 8)
    {
        _buffer = buffer;
        _serializer = serializer;
        _mqtt = mqtt;
        _logger = logger;
        _siteId = string.IsNullOrWhiteSpace(siteId) ? SiteOptions.DefaultSiteId : siteId.Trim();
        _maxConcurrentPublishes = Math.Clamp(maxConcurrentPublishes, MinConcurrentPublishes, MaxConcurrentPublishesCap);
    }

    public async Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct = default)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Forward);
        var roundSw = Stopwatch.StartNew();

        // ── 固定上限限制单次出队量（替代原 AIMD 节流）──
        var takeCount = Math.Min(maxCount, MaxDequeuePerCall);

        var dequeueSw = Stopwatch.StartNew();
        var dequeueResult = await _buffer.DequeueAsync(takeCount, ct);
        dequeueSw.Stop();
        NitroMetrics.ForwardDequeueDurationMs.Observe(dequeueSw.Elapsed.TotalMilliseconds);
        // P1-3①：Dequeue 失败必须显式暴露——否则出队异常被吞掉，转发静默停滞，
        // 批次停留在 Pending 且无任何信号。失败时记录 Error 并返回失败结果。
        if (dequeueResult.IsFailure)
        {
            _logger.LogError("转发出队失败: {Error}", dequeueResult.Error!.Message);
            activity?.SetStatus(ActivityStatusCode.Error, dequeueResult.Error!.Message);
            NitroMetrics.ForwardRoundDurationMs.Observe(roundSw.Elapsed.TotalMilliseconds);
            return OperationResult.Failure(dequeueResult.Error);
        }

        if (dequeueResult.Value!.Count == 0)
        {
            NitroMetrics.BufferBacklog.Set(0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            NitroMetrics.ForwardRoundDurationMs.Observe(roundSw.Elapsed.TotalMilliseconds);
            return OperationResult.Success();
        }

        activity?.SetTag(GatewayActivityTags.BatchSize, dequeueResult.Value!.Count);
        activity?.SetTag(GatewayActivityTags.Concurrency, _maxConcurrentPublishes);

        // 并发发布结果收集：committed 用并发容器；失败计数/首错用原子写。
        // 注意：Activity 的 SetStatus/SetTag 非线程安全，故 worker 内不触碰 activity，收尾统一设置。
        var committed = new ConcurrentBag<Guid>();
        var anyFailure = 0;
        string? firstError = null;
        // 仅异常路径写 tag（与旧实现一致：发布失败只置 status description，不写 ErrorMessage tag）
        string? firstErrorDetail = null;

        using var publishGate = new SemaphoreSlim(_maxConcurrentPublishes, _maxConcurrentPublishes);
        var workers = dequeueResult.Value!.Select(async batch =>
        {
            await publishGate.WaitAsync().ConfigureAwait(false);
            NitroMetrics.ForwardInflight.Inc();
            try
            {
                var payload = _serializer.Serialize(batch);
                // ADR-035 第 1 步：上行 topic 增加站点层，任意订阅端按第三段解析 siteId 区分现场
                var topic = $"nitrogateway/{_siteId}/{batch.DeviceId}/measurements";
                var result = await _mqtt.PublishAsync(topic, payload, qos: 1, ct).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    committed.Add(batch.Id);
                    NitroMetrics.ForwardTotal.WithLabels("success").Inc();
                    NitroMetrics.ForwardPointsTotal.WithLabels("success").Inc(batch.Records.Count);
                }
                else
                {
                    _logger.LogWarning("转发失败 {BatchId}: {Error}", batch.Id, result.Error!.Message);
                    await MarkFailedOrLogErrorAsync(batch.Id, result.Error!.Message, ct).ConfigureAwait(false);
                    NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                    NitroMetrics.ForwardPointsTotal.WithLabels("failure").Inc(batch.Records.Count);
                    Interlocked.Increment(ref anyFailure);
                    Interlocked.CompareExchange(ref firstError, result.Error!.Message, null);
                }
            }
            catch (OperationCanceledException)
            {
                // 上抛让引擎按停机路径处理（正常停机会排空剩余 Pending）；已出队未处理批次
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发异常 {BatchId}", batch.Id);
                await MarkFailedOrLogErrorAsync(batch.Id, ex.Message, ct).ConfigureAwait(false);
                NitroMetrics.ForwardTotal.WithLabels("failure").Inc();
                NitroMetrics.ForwardPointsTotal.WithLabels("failure").Inc(batch.Records.Count);
                Interlocked.Increment(ref anyFailure);
                Interlocked.CompareExchange(ref firstError, ex.Message, null);
                Interlocked.CompareExchange(ref firstErrorDetail, ex.ToString(), null);
            }
            finally
            {
                NitroMetrics.ForwardInflight.Dec();
                publishGate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // 取消：不提交、不标记（已出队未提交批次靠启动恢复重置 InFlight）
            activity?.SetStatus(ActivityStatusCode.Error, "转发轮被取消");
            throw;
        }

        string? commitError = null;
        var committedIds = committed.ToArray();
        if (committedIds.Length > 0 && !ct.IsCancellationRequested)
        {
            // P1-3②：Commit 失败会让已转发批次卡在 InFlight（仅进程重启时恢复），
            // 必须记录 Error 级日志，避免静默丢数。
            var commitSw = Stopwatch.StartNew();
            var commitResult = await _buffer.CommitAsync(committedIds, ct);
            commitSw.Stop();
            NitroMetrics.ForwardCommitDurationMs.Observe(commitSw.Elapsed.TotalMilliseconds);
            if (commitResult.IsFailure)
            {
                _logger.LogError("转发批次提交失败 {Count} 批: {Error}", committedIds.Length, commitResult.Error!.Message);
                Interlocked.Increment(ref anyFailure);
                commitError = commitResult.Error!.Message;
            }
        }

        NitroMetrics.BufferBacklog.Set(await _buffer.GetCountAsync(ct));
        NitroMetrics.BufferBacklogByChannel
            .WithLabels(IForwardBuffer.MqttChannel)
            .Set(await _buffer.GetCountAsync(IForwardBuffer.MqttChannel, ct));

        activity?.SetTag(GatewayActivityTags.CommitCount, committedIds.Length);
        activity?.SetTag(GatewayActivityTags.FailureCount, Volatile.Read(ref anyFailure));

        // 成功路径才置 Ok；任一批次失败/异常/提交失败置 Error（提交失败优先展示）。
        if (Volatile.Read(ref anyFailure) == 0)
            activity?.SetStatus(ActivityStatusCode.Ok);
        else
            activity?.SetStatus(ActivityStatusCode.Error, commitError ?? firstError);
        if (firstErrorDetail is not null)
            activity?.SetTag(GatewayActivityTags.ErrorMessage, firstErrorDetail);

        NitroMetrics.ForwardRoundDurationMs.Observe(roundSw.Elapsed.TotalMilliseconds);
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
