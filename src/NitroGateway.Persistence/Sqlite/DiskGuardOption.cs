namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// 磁盘守卫配置（appsettings 的 "Disk" 段，ADR-012）。默认值保证零配置可用：
/// Warning 1GB / Critical 256MB / 周期 60s。
/// </summary>
public sealed class DiskGuardOption
{
    /// <summary>配置节名（appsettings 中为 "Disk"）</summary>
    public const string SectionName = "Disk";

    /// <summary>检查周期（秒），默认 60；夹紧到 ≥5（避免测试/运维配置过频）</summary>
    public int IntervalSeconds { get; init; } = 60;

    /// <summary>Warning 阈值：剩余空间低于此字节数告警（默认 1GB）</summary>
    public long WarningFreeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Critical 阈值：剩余空间低于此字节数降级（默认 256MB）</summary>
    public long CriticalFreeBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>恢复滞后比例（%）：进入阈值后需恢复到阈值 ×(1+比例) 才解除，防临界抖动（默认 20%）</summary>
    public double RecoveryMarginPercent { get; init; } = 20;
}
