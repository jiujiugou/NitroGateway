namespace NitroGateway.Transport.MQTT;

public interface IMqttStateListener
{
    /// <summary>连接状态变更通知</summary>
    /// <param name="state">新状态</param>
    /// <param name="ct">取消令牌</param>
    ValueTask OnStateChangedAsync(
        MqttConnectionState state,
        CancellationToken ct = default);
}
