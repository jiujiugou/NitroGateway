using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.Messaging;

/// <summary>
/// 一帧 UI 数据（ADR-026 D2）。由 <see cref="EventBridge"/> 每 200ms 合并一次，
/// 携带本帧内的点位快照、设备健康变更、MQTT 状态与缓冲水位（水位每 2s 刷新一次）。
/// <para>字段按需填充：无数据的分组为空集合或 <c>null</c>，消费方需自行判空跳过。</para>
/// </summary>
public sealed record UiFrame
{
    /// <summary>本帧内的点位快照（按事件到达顺序）</summary>
    public IReadOnlyList<PointSnapshot> Measurements { get; init; } = [];

    /// <summary>本帧内的设备健康变更</summary>
    public IReadOnlyList<DeviceHealthChanged> HealthChanges { get; init; } = [];

    /// <summary>最新 MQTT 连接状态；本帧无状态变更时为 <c>null</c>。</summary>
    public MqttConnectionState? MqttState { get; init; }

    /// <summary>转发缓冲积压批数（本轮有刷新且变化时为值，否则 null）</summary>
    public int? BufferBacklog { get; init; }

    /// <summary>帧是否为空（无任何数据，跳过发布）</summary>
    public bool IsEmpty =>
        Measurements.Count == 0 && HealthChanges.Count == 0 &&
        MqttState is null && BufferBacklog is null;
}

