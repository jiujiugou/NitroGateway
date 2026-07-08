namespace NitroGateway.DeviceManagement;

/// <summary>设备健康快照，面向运维面板和告警系统查询</summary>
public sealed record DeviceHealthSnapshot
{
    /// <summary>设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>设备运行状态</summary>
    public Domain.Devices.DeviceStatus Status { get; init; }

    /// <summary>上次采集时间（UTC）</summary>
    public DateTime? LastCollectionAt { get; init; }

    /// <summary>当前连续失败次数</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>当前连续成功次数</summary>
    public int ConsecutiveSuccesses { get; init; }

    /// <summary>最后一次错误信息</summary>
    public string? LastError { get; init; }
}
