using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Scheduler;

/// <summary>简单调度器实现。单线程轮询，不依赖 Quartz</summary>
public sealed class SchedulerEngine : IScheduler
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ILogger<SchedulerEngine> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SchedulerEngine(ILogger<SchedulerEngine> logger) => _logger = logger;

    /// <inheritdoc />
    public void Register(string name, int intervalMs, Func<CancellationToken, Task> job)
    {
        _jobs[name] = new Job(name, intervalMs, 0, job);
        _logger.LogInformation("已注册调度任务: {Name} (每 {Interval}ms)", name, intervalMs);
    }

    /// <inheritdoc />
    public void Unregister(string name)
    {
        _jobs.TryRemove(name, out _);
        _logger.LogInformation("已注销调度任务: {Name}", name);
    }

    /// <inheritdoc />
    public Task RunAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunLoopAsync(_cts.Token);
        return _loop;
    }

    /// <inheritdoc />
    public void Stop()
    {
        _cts?.Cancel();
        _logger.LogInformation("调度器已停止");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = Environment.TickCount64;

            foreach (var (_, job) in _jobs)
            {
                if (now - job.LastRunMs < job.IntervalMs)
                    continue;

                job.LastRunMs = now;

                try
                {
                    _logger.LogDebug("执行调度任务: {Name}", job.Name);
                    await job.Action(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "调度任务异常: {Name}", job.Name);
                }
            }

            await Task.Delay(100, ct);
        }
    }

    private sealed class Job
    {
        public string Name { get; }
        public int IntervalMs { get; }
        public long LastRunMs { get; set; }
        public Func<CancellationToken, Task> Action { get; }

        public Job(string name, int intervalMs, long lastRunMs, Func<CancellationToken, Task> action)
        {
            Name = name;
            IntervalMs = intervalMs;
            LastRunMs = lastRunMs;
            Action = action;
        }
    }
}
