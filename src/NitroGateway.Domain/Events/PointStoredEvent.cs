using NitroGateway.Domain.Devices;

namespace NitroGateway.Domain.Events;

/// <summary>
/// 点位数据已存储事件。由 Dispatcher 在数据写入后发布。
/// 订阅方（Alarm、Statistics、Audit 等）通过实现 <see cref="IPointStoredSink"/> 接收。
/// </summary>
public sealed record PointStoredEvent
{
    /// <summary>设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>本轮采集的快照</summary>
    public IReadOnlyList<PointSnapshot> Snapshots { get; init; } = Array.Empty<PointSnapshot>();

    /// <summary>
    /// 实际落库/转发（及 SignalR 推送）的放行子集（ADR-053 变化抑制）。
    /// <para>语义：<see cref="Snapshots"/> 永远是本轮全量（桌面实时图/告警照收），
    /// 本属性只含「首样本 + 变化点 + 心跳 + 质量变化」；null 表示未启用抑制
    /// （兼容旧调用方，此时应回退到 <see cref="Snapshots"/> 全量）。</para>
    /// </summary>
    public IReadOnlyList<PointSnapshot>? PersistedSnapshots { get; init; }
}
