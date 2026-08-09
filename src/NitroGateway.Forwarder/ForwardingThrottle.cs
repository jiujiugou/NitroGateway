namespace NitroGateway.Forwarder;

/// <summary>
/// 转发节流器。自适应调节批量大小和批次间延迟，防止 MQTT 恢复瞬间冲垮 Broker。
/// <para>策略（AIMD 自适应）：</para>
/// <list type="bullet">
/// <item>失败（乘性减）：批量大小减半（下限 100），延迟 +20ms（上限 200ms）；</item>
/// <item>成功（加性增）：批量大小 +10（上限 1000，即初始值），延迟 -5ms（下限 0）。</item>
/// </list>
/// <para>
/// 收紧远快于恢复：失败减半可在数轮内把压力降到安全区间，成功 +10 缓慢恢复避免抖动。
/// 线程安全：本类不设锁，依赖 Forwarder 单线程顺序调用（每轮仅一个转发循环逐个反馈成功/失败）。
/// ADR-001 P3-14：节流状态全局共享（单设备故障会拖慢全部设备）——v1 单 Broker 场景可接受，暂不按设备隔离。
/// </para>
/// </summary>
public sealed class ForwardingThrottle
{
    /// <summary>
    /// 当前允许的最大单次出队批量大小。初始 1000；失败减半（下限 100），成功 +10（上限 1000）。
    /// 该值与批量内实际出队量共同决定单轮排水压力。
    /// </summary>
    public int MaxBatchSize { get; private set; } = 1000;

    /// <summary>
    /// 当前每批之间的延迟（毫秒）。初始 0（不延迟）；失败 +20ms（上限 200），成功 -5ms（下限 0）。
    /// 仅当大于 0 时 <see cref="ApplyDelayAsync"/> 实际等待。
    /// </summary>
    public int DelayMs { get; private set; }

    /// <summary>MQTT 发布失败时调用：收紧节流——批量减半（下限 100）、延迟 +20ms（上限 200）</summary>
    public void OnMqttFailure()
    {
        MaxBatchSize = Math.Max(100, MaxBatchSize / 2);
        DelayMs = Math.Min(200, DelayMs + 20);
    }

    /// <summary>MQTT 发布成功时调用：放松节流——批量 +10（上限 1000）、延迟 -5ms（下限 0）</summary>
    public void OnMqttSuccess()
    {
        MaxBatchSize = Math.Min(1000, MaxBatchSize + 10);
        DelayMs = Math.Max(0, DelayMs - 5);
    }

    /// <summary>应用批次间延迟：仅当 <see cref="DelayMs"/> 大于 0（节流生效）时等待，否则立即返回</summary>
    /// <param name="ct">取消令牌；等待期间取消会抛出 OperationCanceledException，由调用方按停机路径处理</param>
    public async Task ApplyDelayAsync(CancellationToken ct = default)
    {
        if (DelayMs > 0)
            await Task.Delay(DelayMs, ct);
    }
}
