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
