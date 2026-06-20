namespace NitroGateway.Domain.Devices;

/// <summary>
/// 设备点位定义，描述设备上一个数据点的静态配置信息。
/// 运行时采集到的值由 <see cref="PointSnapshot"/> 承载，与本定义对象分离。
/// </summary>
public sealed class DevicePoint
{
    /// <summary>点位唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>点位名称，如 "炉温"、"转速"</summary>
    public required string Name { get; set; }

    /// <summary>
    /// 映射到协议的地址表达式。
    /// 示例：Modbus 保持寄存器 "40001"、OPC UA NodeId "ns=3;s=Temperature"、S7 "DB1.DBD0"
    /// </summary>
    public required string Address { get; set; }

    /// <summary>点位描述</summary>
    public string? Description { get; set; }

    /// <summary>数据类型</summary>
    public DataType DataType { get; set; }

    /// <summary>是否启用采集</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>读写权限</summary>
    public PointAccess Access { get; set; } = PointAccess.ReadOnly;

    /// <summary>采集间隔（毫秒）。0 表示继承设备默认间隔</summary>
    public int ScanIntervalMs { get; set; }

    /// <summary>
    /// 值变化死区，仅对模拟量（Float、Double 等）有效。
    /// 相邻两次采集值的变化小于此值时不触发上报，0 表示不启用死区。
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>缩放系数。工程值 = 原始值 × ScaleFactor + ScaleOffset</summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>缩放偏移</summary>
    public double ScaleOffset { get; set; }
}
