namespace NitroGateway.Forwarder;

/// <summary>
/// 转发节流器。自适应调节批量大小和批次间延迟，防止 MQTT 恢复瞬间冲垮 Broker。
///
/// 策略（AIMD 自适应）：
/// - MQTT 失败：批量大小减半（下限 100），延迟递增（上限 200ms）
/// - MQTT 成功：批量大小缓慢恢复（+10，上限 1000），延迟递减（-5ms，下限 0）
///
/// 无状态锁 —— Forwarder 由 Scheduler 单线程调用。
/// </summary>
public sealed class ForwardingThrottle
{
    /// <summary>当前允许的最大单次出队批量大小</summary>
    public int MaxBatchSize { get; private set; } = 1000;

    /// <summary>当前批次间延迟（毫秒）</summary>
    public int DelayMs { get; private set; }

    /// <summary>MQTT 发布失败时调用：收紧节流</summary>
    public void OnMqttFailure()
    {
        MaxBatchSize = Math.Max(100, MaxBatchSize / 2);
        DelayMs = Math.Min(200, DelayMs + 20);
    }

    /// <summary>MQTT 发布成功时调用：放松节流</summary>
    public void OnMqttSuccess()
    {
        MaxBatchSize = Math.Min(1000, MaxBatchSize + 10);
        DelayMs = Math.Max(0, DelayMs - 5);
    }

    /// <summary>应用批次间延迟（如果节流生效）</summary>
    public async Task ApplyDelayAsync(CancellationToken ct = default)
    {
        if (DelayMs > 0)
            await Task.Delay(DelayMs, ct);
    }
}
