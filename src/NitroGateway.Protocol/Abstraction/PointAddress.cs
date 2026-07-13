namespace NitroGateway.Protocols;

/// <summary>协议无关的地址抽象基类。各协议提供强类型子类</summary>
public abstract record PointAddress
{
    /// <summary>原始地址字符串，如 "40001"、"DB1.DBD0"</summary>
    public required string Raw { get; init; }
}
