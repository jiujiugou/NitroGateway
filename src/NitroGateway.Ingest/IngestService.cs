using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Ingest;

/// <summary>
/// 中心订阅入库服务（ADR-025 P0）。
/// 启动后先订阅契约主题（重连由 MqttClientWrapper 重放，ADR-006 P1-2），
/// 再消费消息流：遥测批量 INSERT OR IGNORE、告警 UPSERT；写失败重试 3 次指数退避后丢弃并计数（D5）。
/// </summary>
public sealed class IngestService : BackgroundService
{
    /// <summary>遥测上行主题（ADR-025 契约：Forwarder.cs 现成 topic + BatchMeasurements JSON）</summary>
    public const string MeasurementsTopicFilter = "nitrogateway/+/measurements";

    /// <summary>告警上行主题（MqttAlarmNotifier 现成 topic，ADR-028 P2-1 契约对齐）</summary>
    public const string AlarmsTopicFilter = "nitrogateway/+/alarms";

    /// <summary>指标 kind 标签：遥测</summary>
    public const string KindMeasurements = "measurements";

    /// <summary>指标 kind 标签：告警</summary>
    public const string KindAlarms = "alarms";

    /// <summary>写失败重试次数（D5：3 次指数退避）</summary>
    private const int WriteRetryAttempts = 3;

    /// <summary>反序列化选项：与 JsonMessageSerializer 对齐（camelCase，枚举按数字）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IMqttClient _mqtt;
    private readonly IIngestStore _store;
    private readonly ILogger<IngestService> _logger;
    private readonly TimeSpan _retryBaseDelay;

    /// <summary>创建订阅入库服务</summary>
    /// <param name="mqtt">MQTT 客户端（订阅 + 消息流）</param>
    /// <param name="store">中心入库实现</param>
    /// <param name="logger">日志</param>
    /// <param name="retryBaseDelay">写失败重试退避基数（测试注入零值加速）</param>
    public IngestService(IMqttClient mqtt, IIngestStore store, ILogger<IngestService> logger,
        TimeSpan? retryBaseDelay = null)
    {
        _mqtt = mqtt;
        _store = store;
        _logger = logger;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// 执行主体：先确保已连接并完成契约订阅，再消费消息流。
    /// 单消息异常只记日志不中断消费；取消（停机）正常退出。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await EnsureSubscribedAsync(ct);

        await foreach (var message in _mqtt.Messages.WithCancellation(ct))
        {
            try
            {
                await ProcessMessageAsync(message, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 兜底：任何未预期异常不得击穿消费循环
                _logger.LogError(ex, "处理 MQTT 消息异常（不中断消费）: {Topic}", message.Topic);
                NitroMetrics.IngestFailuresTotal.WithLabels(KindMeasurements).Inc();
            }
        }
    }

    /// <summary>
    /// 确保订阅（含首连）：未连接时轮询等待；订阅失败按周期重试。
    /// 成功后交由 MqttClientWrapper 在断线重连时重放（ADR-006 P1-2），此处不做重复订阅。
    /// </summary>
    private async Task EnsureSubscribedAsync(CancellationToken ct)
    {
        var topics = new[] { MeasurementsTopicFilter, AlarmsTopicFilter };
        while (!ct.IsCancellationRequested)
        {
            if (_mqtt.State == MqttConnectionState.Connected)
            {
                var allOk = true;
                foreach (var topic in topics)
                {
                    var r = await _mqtt.SubscribeAsync(topic, qos: 1, ct);
                    if (r.IsFailure)
                    {
                        _logger.LogWarning("中心订阅失败: {Topic} - {Error}", topic, r.Error?.Message);
                        allOk = false;
                        break;
                    }
                }

                if (allOk)
                {
                    _logger.LogInformation("中心已订阅契约主题: {Topics}", string.Join(", ", topics));
                    return;
                }
            }

            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>按主题后缀路由到遥测/告警处理（测试经此方法驱动）</summary>
    internal Task ProcessMessageAsync(MqttMessage message, CancellationToken ct)
    {
        if (message.Topic.EndsWith("/measurements"))
            return ProcessMeasurementsAsync(message, ct);
        if (message.Topic.EndsWith("/alarms"))
            return ProcessAlarmAsync(message, ct);

        _logger.LogDebug("忽略未订阅主题消息: {Topic}", message.Topic);
        return Task.CompletedTask;
    }

    // ═══════════ 遥测 ═══════════

    /// <summary>
    /// 遥测消息处理：反序列化 → 批量 INSERT OR IGNORE → 指标/日志。
    /// 反序列化失败与空批次视为坏消息：丢弃 + failure 计数，不阻塞后续消息。
    /// </summary>
    private async Task ProcessMeasurementsAsync(MqttMessage message, CancellationToken ct)
    {
        BatchMeasurements? batch;
        try
        {
            batch = JsonSerializer.Deserialize<BatchMeasurements>(message.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("遥测消息反序列化失败（丢弃）: {Topic} - {Error}", message.Topic, ex.Message);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindMeasurements).Inc();
            return;
        }

        if (batch is null || batch.Records.Count == 0)
        {
            _logger.LogWarning("遥测消息为空批次（丢弃）: {Topic}", message.Topic);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindMeasurements).Inc();
            return;
        }

        NitroMetrics.IngestReceivedTotal.WithLabels(KindMeasurements).Inc();

        var result = await WithRetryAsync(
            ct => _store.WriteMeasurementsAsync(batch.Records, ct), ct);

        if (result.IsFailure)
        {
            _logger.LogError("遥测入库失败（重试 {Attempts} 次后丢弃）: {BatchId} - {Error}",
                WriteRetryAttempts, batch.Id, result.Error?.Message);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindMeasurements).Inc();
            return;
        }

        var write = result.Value!;
        if (write.DeduplicatedCount > 0)
            NitroMetrics.IngestDedupTotal.WithLabels(KindMeasurements).Inc(write.DeduplicatedCount);

        _logger.LogInformation("遥测入库: 批次 {BatchId} 记录 {Received} 新增 {Inserted} 去重 {Dedup}",
            batch.Id, write.ReceivedCount, write.InsertedCount, write.DeduplicatedCount);
    }

    // ═══════════ 告警 ═══════════

    /// <summary>
    /// 告警消息处理：反序列化 → UPSERT 中心 alarms（状态迁移覆盖，ADR-028 P2-1）。
    /// 坏消息（反序列化失败/空 ID）丢弃并计数。
    /// </summary>
    private async Task ProcessAlarmAsync(MqttMessage message, CancellationToken ct)
    {
        IngestAlarmMessage? alarm;
        try
        {
            alarm = JsonSerializer.Deserialize<IngestAlarmMessage>(message.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("告警消息反序列化失败（丢弃）: {Topic} - {Error}", message.Topic, ex.Message);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindAlarms).Inc();
            return;
        }

        if (alarm is null || alarm.AlarmId == Guid.Empty)
        {
            _logger.LogWarning("告警消息无效（丢弃）: {Topic}", message.Topic);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindAlarms).Inc();
            return;
        }

        NitroMetrics.IngestReceivedTotal.WithLabels(KindAlarms).Inc();

        var result = await WithRetryAsync(ct => _store.UpsertAlarmAsync(alarm, ct), ct);

        if (result.IsFailure)
        {
            _logger.LogError("告警入库失败（重试 {Attempts} 次后丢弃）: {AlarmId} - {Error}",
                WriteRetryAttempts, alarm.AlarmId, result.Error?.Message);
            NitroMetrics.IngestFailuresTotal.WithLabels(KindAlarms).Inc();
            return;
        }

        _logger.LogInformation("告警入库/更新: {AlarmId} 状态 {State}", alarm.AlarmId, alarm.State);
    }

    // ═══════════ 重试（D5） ═══════════

    /// <summary>
    /// 写失败重试（带返回值）：最多 <see cref="WriteRetryAttempts"/> 次，指数退避（基数 500ms，测试可注入零值）。
    /// 重试耗尽返回最后一次失败结果，由调用方丢弃 + 计数（中心故障暴露给运维而非阻塞链路）。
    /// </summary>
    private async Task<OperationResult<T>> WithRetryAsync<T>(
        Func<CancellationToken, Task<OperationResult<T>>> action, CancellationToken ct)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= WriteRetryAttempts; attempt++)
        {
            OperationResult<T> result;
            try
            {
                result = await action(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 仓储已归类，此处兜底未预期异常（正常不会走到）
                lastError = ex.Message;
                result = OperationResult<T>.Failure(OperationalError.General(ex.Message));
            }

            if (result.IsSuccess) return result;
            lastError = result.Error?.Message ?? "未知错误";

            if (attempt < WriteRetryAttempts)
            {
                try { await Task.Delay(_retryBaseDelay * (1 << (attempt - 1)), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        return OperationResult<T>.Failure(OperationalError.General(
            $"写入重试 {WriteRetryAttempts} 次仍失败: {lastError}"));
    }

    /// <summary>
    /// 写失败重试（无返回值版本，供告警 UPSERT 使用），语义与带返回值版本一致。
    /// </summary>
    private async Task<OperationResult> WithRetryAsync(
        Func<CancellationToken, Task<OperationResult>> action, CancellationToken ct)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= WriteRetryAttempts; attempt++)
        {
            OperationResult result;
            try
            {
                result = await action(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                result = OperationResult.Failure(OperationalError.General(ex.Message));
            }

            if (result.IsSuccess) return result;
            lastError = result.Error?.Message ?? "未知错误";

            if (attempt < WriteRetryAttempts)
            {
                try { await Task.Delay(_retryBaseDelay * (1 << (attempt - 1)), ct); }
                catch (OperationCanceledException) { throw; }
            }
        }

        return OperationResult.Failure(OperationalError.General(
            $"写入重试 {WriteRetryAttempts} 次仍失败: {lastError}"));
    }
}
