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

    /// <summary>
    /// 配置最后修改时间（UTC，ADR-033 阶段 3/4 同步版本依据）。
    /// 仓储保存时自动盖章（缺省值→当前时间）；同步合并时保留来源时间戳。
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>删除标记（tombstone，ADR-033 阶段 3/4）：中心侧点位删除=权威删除</summary>
    public bool IsDeleted { get; set; }

    /// <summary>采集间隔（毫秒）。0 表示继承设备默认间隔</summary>
    public int ScanIntervalMs { get; set; }

    /// <summary>
    /// 变化抑制阈值（死区），仅对模拟量（Float、Double 等）有效（ADR-053）。
    /// 0（默认）= 每样本都上报/落库/推送（向后兼容，需每秒连续历史的点保持 0）；
    /// &gt; 0 = |新工程值 − 最后已存值| <strong>&lt;</strong> 此值时抑制（不落库、不转发、不推送），
    /// 达到或超过此值才上报；另有心跳兜底（<c>Collection:DeadbandHeartbeatMs</c>，默认 5 分钟）
    /// 保证长期静止的点也会周期性补一条，避免断档。
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>缩放系数。工程值 = 原始值 × ScaleFactor + ScaleOffset</summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>缩放偏移</summary>
    public double ScaleOffset { get; set; }
}
