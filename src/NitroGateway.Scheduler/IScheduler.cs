namespace NitroGateway.Scheduler;

/// <summary>通用任务调度器</summary>
public interface IScheduler
{
    /// <summary>注册一个定时任务。intervalMs = 0 表示执行一次</summary>
    void Register(string name, int intervalMs, Func<CancellationToken, Task> job);

    /// <summary>注销任务</summary>
    void Unregister(string name);

    /// <summary>启动调度器（阻塞当前线程直到 Stop 或 cancellation）</summary>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>停止调度器</summary>
    void Stop();
}
