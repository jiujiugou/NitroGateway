namespace NitroGateway.Security.Guard;

/// <summary>写指令 DTO。由 Controller 绑定后传入 WriteGuard 校验</summary>
public sealed record WriteCommand
{
    /// <summary>目标设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>目标点位 ID</summary>
    public Guid PointId { get; init; }

    /// <summary>写入值</summary>
    public double Value { get; init; }

    /// <summary>点位数据类型</summary>
    public string DataType { get; init; } = "Float";

    /// <summary>设备当前状态</summary>
    public string DeviceStatus { get; init; } = "Online";

    /// <summary>上次写入值（用于变化率校验，null 表示首次写入）</summary>
    public double? PreviousValue { get; init; }

    /// <summary>点位配置的最大值（如果配了 Range 限制）</summary>
    public double? MaxLimit { get; init; }

    /// <summary>点位配置的最小值（如果配了 Range 限制）</summary>
    public double? MinLimit { get; init; }
}
