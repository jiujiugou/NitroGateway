namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 协议驱动能力声明。
/// 每种协议有不同的特性集，采集引擎据此选择最优调用策略。
/// </summary>
public sealed class DriverCapability
{
    /// <summary>是否支持批量读取（一次请求读取多个点位）</summary>
    public bool SupportsBatchRead { get; init; }

    /// <summary>是否支持批量写入（一次请求写入多个点位）</summary>
    public bool SupportsBatchWrite { get; init; }

    /// <summary>是否支持订阅推送（服务端主动上报数据变更），OPC UA 支持，Modbus 不支持</summary>
    public bool SupportsSubscription { get; init; }

    /// <summary>单次批量请求的最大点位数量。0 表示无限制</summary>
    public int MaxBatchSize { get; init; }
}
