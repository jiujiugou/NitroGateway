namespace NitroGateway.Transport.MQTT;

/// <summary>
/// MQTT 连接状态监听者（SignalR 推送等）。
/// ADR-020 P1-1：由 <see cref="MqttClientWrapper"/> 在每次状态变更时通知（fire-and-forget，异常隔离）。
/// </summary>
public interface IMqttStateListener
{
    /// <summary>连接状态变更通知</summary>
    /// <param name="state">新状态</param>
    /// <param name="ct">取消令牌</param>
    ValueTask OnStateChangedAsync(
        MqttConnectionState state,
        CancellationToken ct = default);
}
