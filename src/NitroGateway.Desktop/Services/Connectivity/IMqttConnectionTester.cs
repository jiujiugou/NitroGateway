using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.Services.Connectivity;

/// <summary>MQTT Broker 连接测试结果。</summary>
/// <param name="Success">是否连通并成功发布测试消息。</param>
/// <param name="ElapsedMs">耗时（毫秒）。</param>
/// <param name="Message">失败原因；成功时为 null。</param>
public sealed record MqttConnectionTestResult(bool Success, long ElapsedMs, string? Message);

/// <summary>
/// MQTT Broker 连接测试服务（设置页「测试连接」按钮）。
/// 用独立临时客户端（绝不使用 DI 单例），避免干扰正在运行的上报/告警连接。
/// </summary>
public interface IMqttConnectionTester
{
    /// <summary>
    /// 测试指定 Broker 是否可连通：Connect 成功后发布一条测试消息（QoS1），
    /// 验证链路 + 凭证 + 写入权限（ADR-020 P3-6：无订阅者按成功处理，不验证消费端）。
    /// </summary>
    /// <param name="host">Broker 地址</param>
    /// <param name="port">端口（1-65535）</param>
    /// <param name="useTls">是否启用 TLS</param>
    /// <param name="username">用户名（可选，空为匿名）</param>
    /// <param name="password">密码（可选）</param>
    /// <param name="ct">取消令牌</param>
    Task<MqttConnectionTestResult> TestAsync(
        string host, int port, bool useTls, string? username, string? password, CancellationToken ct = default);
}
