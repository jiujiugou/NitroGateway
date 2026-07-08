using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Forwarder;

/// <summary>
/// 转发引擎。BackgroundService + PeriodicTimer，定时触发转发。
/// 替代原来通过 IScheduler 注册的方式，与 CollectionEngine 模式统一。
/// </summary>
public sealed class ForwarderEngine : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<ForwarderEngine> _logger;

    /// <summary>创建转发引擎</summary>
    public ForwarderEngine(
        IServiceScopeFactory scopeFactory,
        TimeSpan interval,
        ILogger<ForwarderEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _interval = interval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var forwarder = scope.ServiceProvider.GetRequiredService<IForwarder>();
                await forwarder.ForwardBatchAsync(int.MaxValue, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发循环发生异常。");
            }
        }

        _logger.LogInformation("ForwarderEngine Stopped.");
    }
}
