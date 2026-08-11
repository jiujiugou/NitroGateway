namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 死信条目摘要，用于 Admin API 展示。
/// 有意只携带最小字段（不含设备名）：死信量小，前端按 DeviceId 展示即可；
/// 如需设备名可后续增加冗余字段（实现侧需 join devices 表）。
/// </summary>
public sealed record DeadLetterEntry
{
    /// <summary>批次 ID</summary>
    public Guid BatchId { get; init; }

    /// <summary>所属设备 ID</summary>
    public Guid DeviceId { get; init; }

    /// <summary>本批次记录数</summary>
    public int RecordCount { get; init; }

    /// <summary>已重试次数</summary>
    public int RetryCount { get; init; }

    /// <summary>最后一次失败原因</summary>
    public string? LastError { get; init; }

    /// <summary>原始入队时间（UTC，即批次首次进入转发缓冲的时间，非转死信时刻；ADR-021 P3-6）</summary>
    public DateTime EnqueuedAt { get; init; }
}
