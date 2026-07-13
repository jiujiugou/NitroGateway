using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Host;

namespace NitroGateway.Collection;

/// <summary>采集引擎。BackgroundService + PeriodicTimer，定时触发采集。</summary>
public sealed class CollectionEngine : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GatewayLifecycle _lifecycle;
    private readonly TimeSpan _interval;
    private Task? _currentRound;
    private CancellationTokenSource? _roundCts;
    private readonly ILogger<CollectionEngine> _logger;

    /// <summary>创建采集引擎</summary>
    /// <param name="scopeFactory">DI scope factory</param>
    /// <param name="lifecycle">网关生命周期状态</param>
    /// <param name="interval">采集间隔</param>
    /// <param name="logger">日志记录器</param>
    public CollectionEngine(
        IServiceScopeFactory scopeFactory,
        GatewayLifecycle lifecycle,
        TimeSpan interval,
        ILogger<CollectionEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _lifecycle = lifecycle;
        _interval = interval;
        _logger = logger;
    }

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
                    break;
                }
                finally
                {
                    _roundCts?.Dispose();
                    _roundCts = null;
                    _currentRound = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "采集循环发生异常。");
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
                _roundCts?.Cancel();
                await Task.WhenAny(
                    current,
                    Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                _lifecycle.MarkDraining();
            }
        }

        // MQTT 断连由 Host 层的 GracefulShutdown 统一管理，
        // 不在此处过早断开——ForwarderEngine 可能还在排空缓冲区。
        _lifecycle.MarkStopped();
        await base.StopAsync(cancellationToken);
    }
}
