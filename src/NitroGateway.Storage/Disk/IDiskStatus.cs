namespace NitroGateway.Storage.Disk;

public interface IDiskStatus
{
    /// <summary>当前磁盘健康等级（后台守卫周期刷新，读写线程安全）</summary>
    DiskLevel Level { get; }

    /// <summary>等级变化事件（进入/退出 Warning/Critical 时触发；订阅方用于日志/健康检查联动）</summary>
    event Action<DiskLevel>? Changed;
}
