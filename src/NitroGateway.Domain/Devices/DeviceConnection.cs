namespace NitroGateway.Domain.Devices;

/// <summary>
/// 设备连接参数值对象。
/// 包含网络地址、超时策略、重试策略以及协议特有扩展参数。
/// </summary>
public sealed class DeviceConnection
{
    /// <summary>
    /// 连接地址。
    /// 示例：Modbus TCP "192.168.1.100:502"、串口 "COM3"、OPC UA "opc.tcp://server:4840"
    /// </summary>
    public required string Endpoint { get; set; }

    /// <summary>建立连接超时（毫秒）</summary>
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>单次请求超时（毫秒）</summary>
    public int RequestTimeoutMs { get; set; } = 5000;

    /// <summary>通信失败后的最大重试次数</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>重试间隔（毫秒）</summary>
    public int RetryIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 协议特有参数。
    /// 示例：Modbus "UnitId"=1、"BaudRate"=9600、"Parity"="None"
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = [];
}
