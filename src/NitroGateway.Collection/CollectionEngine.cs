using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NitroGateway.Host;

namespace NitroGateway.Collection;

/// <summary>
/// 采集引擎（编排层）。以 BackgroundService + PeriodicTimer 驱动，按固定间隔触发一轮全量采集：
/// 每轮创建独立 DI scope，经 <see cref="IDeviceCollector.CollectOnceAsync"/> 并行采集所有非维护设备。
/// <para><b>边界：</b>PeriodicTimer 每次 tick 重新开始计时，单轮耗时超过间隔不会造成轮次堆积；
/// 单轮异常被捕获后延迟 <see cref="_errorRetryDelay"/> 重试，不影响引擎存活。</para>
/// <para><b>关闭：</b>见 <see cref="StopAsync"/> 的 drain 策略。</para>
/// </summary>
public sealed class CollectionEngine : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GatewayLifecycle _lifecycle;
    private readonly TimeSpan _interval;
    /// <summary>当前正在执行的一轮采集 Task；null 表示空闲。供 <see cref="StopAsync"/> 等待/取消。</summary>
    private Task? _currentRound;
    /// <summary>当前轮的取消源，链接宿主停止令牌；停止超时时用于取消本轮采集。</summary>
    private CancellationTokenSource? _roundCts;
    private readonly ILogger<CollectionEngine> _logger;
    /// <summary>单轮异常后的重试延迟，默认 5 秒。</summary>
    private readonly TimeSpan _errorRetryDelay;

    /// <summary>创建采集引擎</summary>
    /// <param name="scopeFactory">DI scope 工厂；每轮采集创建独立 scope，隔离 Scoped 依赖（如 <see cref="DeviceCollector"/>）</param>
    /// <param name="lifecycle">网关生命周期；优雅关闭时协调采集→转发 drain 顺序</param>
    /// <param name="interval">采集间隔，两次 tick 之间的时长</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="errorRetryDelay">单轮异常后的重试延迟；默认 5 秒</param>
    public CollectionEngine(
        IServiceScopeFactory scopeFactory,
        GatewayLifecycle lifecycle,
        IOptions<CollectionOption> options,
        ILogger<CollectionEngine> logger,
        TimeSpan? errorRetryDelay = null)
    {
        _scopeFactory = scopeFactory;
        _lifecycle = lifecycle;
        _interval = TimeSpan.FromMilliseconds(options.Value.IntervalMs);
        _logger = logger;
        _errorRetryDelay = errorRetryDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// 主采集循环：按 <see cref="_interval"/> 周期创建 scope 并执行一轮采集。
    /// 保存当前轮 Task/CTS 供 <see cref="StopAsync"/> 优雅等待或超时取消。
    /// </summary>
    /// <param name="stoppingToken">宿主停止令牌；取消时退出循环</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var collector = scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
                    _roundCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    _currentRound = collector.CollectOnceAsync(_roundCts.Token);
                    if (_currentRound != null)
                        await _currentRound;
                }
                catch (OperationCanceledException)
                {
                    // 正常关闭：停止令牌已取消
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮异常不影响引擎存活：记录后延迟重试进入下一轮
                    _logger.LogError(ex, "采集轮次发生异常，5 秒后重试。");
                    try
                    {
                        await Task.Delay(_errorRetryDelay, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // 重试等待期间收到停止信号
                        break;
                    }
                }
                finally
                {
                    _roundCts?.Dispose();
                    _roundCts = null;
                    _currentRound = null;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关闭：PeriodicTimer 等待被停止令牌中断，显式吞掉 ODE，
            // 避免逃逸到 Host.TryExecuteBackgroundServiceAsync（调试器显示 first-chance 异常）。
        }

        _logger.LogInformation("CollectionEngine Stopped.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("CollectionEngine Stopping...");
        _lifecycle.RequestStop();

        var current = _currentRound;
        if (current != null)
        {
            _logger.LogInformation("等待当前采集轮完成...");

            var completed = await Task.WhenAny(
                current,
                Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));

            if (completed != current)
            {
                _logger.LogWarning("当前采集轮超时，开始取消。");
                var roundCts = _roundCts;
                try { roundCts?.Cancel(); } catch (ObjectDisposedException) { }
                await Task.WhenAny(
                    current,
                    Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
        }

        _lifecycle.MarkStopped();
        await base.StopAsync(cancellationToken);
    }
}
