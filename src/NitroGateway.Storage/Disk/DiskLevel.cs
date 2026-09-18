namespace NitroGateway.Storage.Disk;

public enum DiskLevel
{
    /// <summary>空间充足，正常读写</summary>
    Healthy = 0,

    /// <summary>剩余空间低于 Warning 阈值：仅日志 + 指标 + 健康检查 Degraded，不降级</summary>
    Warning = 1,

    /// <summary>剩余空间低于 Critical 阈值：暂停 measurement 写入与转发出队，保护 SQLite 与日志</summary>
    Critical = 2
}
