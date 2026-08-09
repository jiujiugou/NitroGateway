using NitroGateway.Shared;

namespace NitroGateway.Forwarder;

/// <summary>
/// 数据转发服务：Dequeue → Serialize → Send → Commit。
/// <para>语义约定：</para>
/// <list type="bullet">
/// <item>转发成功（MQTT QoS 1 发布成功）才 Commit 删除；失败批次 MarkFailed 累加重试计数，超限自动进死信；</item>
/// <item>单批失败不抛出异常，通过缓冲的重试/死信机制表达，调用方无需感知个别批次结果；</item>
/// <item>实现内嵌自适应节流，调用方只需定时触发，无需关心 Broker 压力。</item>
/// </list>
/// </summary>
public interface IForwarder
{
    /// <summary>
    /// 处理一批待转发数据：从转发缓冲出队最多 <paramref name="maxCount"/> 批（受节流上限约束），
    /// 逐批序列化并发布到 MQTT（QoS 1），成功批次统一 Commit 删除，失败批次 MarkFailed 进入重试/死信。
    /// </summary>
    /// <param name="maxCount">本次允许出队的最大批次数（上限值；实际取 min(maxCount, 节流当前批量上限)）</param>
    /// <param name="ct">取消令牌，透传给缓冲、MQTT 发布与节流延迟等异步调用</param>
    /// <returns>出队失败返回 Failure（缓冲原状保留、下轮重试）；其余情况返回 Success，即使个别批次转发失败也已按重试/死信策略落库</returns>
    Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct = default);
}
