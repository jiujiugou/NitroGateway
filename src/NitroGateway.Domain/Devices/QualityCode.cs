namespace NitroGateway.Domain.Devices;

/// <summary>
/// 数据质量标记，遵循 OPC UA 数据质量规范。
/// 用于标识采集数据的可靠程度。
/// </summary>
public enum QualityCode
{
    /// <summary>数据正常，可信</summary>
    Good,

    /// <summary>数据来源不确定（如传感器老化、通信间歇中断后首次恢复）</summary>
    Uncertain,

    /// <summary>数据异常，不可信（如采集失败、超时、校验错误）</summary>
    Bad
}
