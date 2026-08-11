namespace NitroGateway.Storage.Disk;

/// <summary>
/// 磁盘健康等级（ADR-012 磁盘保护）。由 <c>DiskGuardService</c> 按周期评估，
/// 采集/转发热路径据此决定是否降级：Warning 仅告警，Critical 暂停写入与出队。
/// </summary>
public enum DiskLevel
{
    /// <summary>空间充足，正常读写</summary>
    Healthy = 0,

    /// <summary>剩余空间低于 Warning 阈值：仅日志 + 指标 + 健康检查 Degraded，不降级</summary>
    Warning = 1,

    /// <summary>剩余空间低于 Critical 阈值：暂停 measurement 写入与转发出队，保护 SQLite 与日志</summary>
    Critical = 2
}
