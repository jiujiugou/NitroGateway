namespace NitroGateway.Protocols;

/// <summary>协议地址解析器。每种协议提供一个实现</summary>
public interface IAddressParser
{
    /// <summary>解析原始地址字符串 → 协议特化 PointAddress 子类</summary>
    PointAddress Parse(string rawAddress);

    /// <summary>序列化回原始地址字符串</summary>
    string Serialize(PointAddress address);

    /// <summary>
    /// 计算两个地址的距离，用于批量优化判断是否可合并。
    /// 返回 -1 表示不可比（不同类型或不同功能区）。
    /// 返回 0 表示紧邻，可合并为一次批量读。
    /// </summary>
    int GetDistance(PointAddress a, PointAddress b);
}
