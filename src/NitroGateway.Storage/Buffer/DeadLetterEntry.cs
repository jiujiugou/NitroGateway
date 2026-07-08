namespace NitroGateway.Storage.Buffer;

/// <summary>死信条目摘要，用于 Admin API 展示</summary>
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

    /// <summary>入队时间</summary>
    public DateTime EnqueuedAt { get; init; }
}
