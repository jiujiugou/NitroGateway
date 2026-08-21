namespace NitroGateway.Protocols.OpcUa;

/// <summary>向复合工厂注册 OPC UA 驱动。由 AddNitroProtocol 调用</summary>
public static class OpcUaRegistration
{
    /// <summary>
    /// 注册 "OPC UA" 协议 → OpcUaDriver。
    /// 注册键与 <c>ProtocolIdentifier.OpcUa.Name</c>（"OPC UA"，含空格）保持一致——
    /// 工厂查找按 <see cref="StringComparison.OrdinalIgnoreCase"/> 忽略大小写但区分空格，
    /// 键不一致会导致 Create 抛 NotSupportedException。
    /// </summary>
    public static void Register(ProtocolDriverFactory factory)
    {
        factory.Register("OPC UA", (_, conn, logger) => new OpcUaDriver(conn, logger));
    }
}
