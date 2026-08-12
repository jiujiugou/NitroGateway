namespace NitroGateway.Domain.Devices;

/// <summary>
/// 工业设备实体，代表网关接入的一台物理或逻辑设备。
/// 一台设备包含多个 <see cref="DevicePoint"/>，通过 <see cref="ProtocolIdentifier"/> 和 <see cref="DeviceConnection"/> 定义接入方式。
/// </summary>
public sealed class Device
{
    /// <summary>设备唯一标识</summary>
    public Guid Id { get; init; }

    /// <summary>设备名称，如 "1号车间 PLC"</summary>
    public required string Name { get; set; }

    /// <summary>设备描述</summary>
    public string? Description { get; set; }

    /// <summary>设备使用的协议</summary>
    public required ProtocolIdentifier Protocol { get; set; }

    /// <summary>连接参数（地址、超时、重试等）</summary>
    public required DeviceConnection Connection { get; set; }

    /// <summary>当前通信状态</summary>
    public DeviceStatus Status { get; set; }

    /// <summary>设备所属站点（ADR-035 方案 A：单一归属；空串=未标注/旧数据）。中心导出/下发按此过滤。</summary>
    public string SiteId { get; set; } = "";

    /// <summary>
    /// 配置最后修改时间（UTC，ADR-033 阶段 3/4 同步版本依据）。
    /// 仓储保存时自动盖章（缺省值→当前时间）；同步下发/合并时保留来源时间戳（中心时钟为准）。
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// 删除标记（tombstone，ADR-033 阶段 3/4）：中心侧删除=权威删除（软删保行），
    /// 同步导出携带以驱动现场删除；现场运行库硬删不保留该标记。
    /// </summary>
    public bool IsDeleted { get; set; }

    private readonly List<DevicePoint> _points = [];

    /// <summary>该设备下的所有点位（只读集合）</summary>
    public IReadOnlyCollection<DevicePoint> Points => _points;

    /// <summary>向设备添加一个采集点位</summary>
    public void AddPoint(DevicePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        _points.Add(point);
    }

    /// <summary>从设备移除指定点位</summary>
    /// <param name="pointId">点位唯一标识</param>
    public void RemovePoint(Guid pointId)
    {
        _points.RemoveAll(p => p.Id == pointId);
    }
}

