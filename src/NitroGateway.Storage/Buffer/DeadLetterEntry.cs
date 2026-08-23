namespace NitroGateway.Storage.Buffer;

/// <summary>
/// 【停用】死信条目摘要（接口只增不删保留；死信特性已移除 2026-08-22，不再产生新死信）。
/// 携带最小字段（不含设备名），供遗留展示按 DeviceId 关联。
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
