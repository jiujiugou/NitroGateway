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
    Task<MqttConnectionTestResult> TestAsync(
        string host, int port, bool useTls, string? username, string? password, CancellationToken ct = default);
}
