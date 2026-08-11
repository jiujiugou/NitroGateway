namespace NitroGateway.Storage.Disk;

/// <summary>
/// 磁盘状态只读接口（ADR-012）。由 Persistence 的 <c>DiskGuardService</c> 实现并周期刷新；
/// 采集/转发热路径只读 <see cref="Level"/> 决定是否降级，不做磁盘 IO。
/// 接口只增不删（AGENTS.md 雷区）。
/// </summary>
public interface IDiskStatus
{
    /// <summary>当前磁盘健康等级（后台守卫周期刷新，读写线程安全）</summary>
    DiskLevel Level { get; }

    /// <summary>等级变化事件（进入/退出 Warning/Critical 时触发；订阅方用于日志/健康检查联动）</summary>
    event Action<DiskLevel>? Changed;
}
