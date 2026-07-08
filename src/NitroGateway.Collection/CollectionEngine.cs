using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Host;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry.Tracing;
using NitroGateway.Transport.MQTT;
namespace NitroGateway.Collection;

/// <summary>采集引擎。串联 DeviceReader → Pipeline → Dispatcher → HealthReporter</summary>
public sealed class CollectionEngine : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMqttClient _mqttClient;
    private Task? _currentRound;
    private GatewayLifecycle _lifecycle;
    private CancellationTokenSource? _roundCts;
    private readonly ILogger<CollectionEngine> _logger;

    /// <summary>创建采集引擎</summary>
    public CollectionEngine(
        IServiceScopeFactory scopeFactory,
        IMqttClient mqttClient,
        GatewayLifecycle lifecycle,
        IForwardBuffer forwardBuffer,
        ILogger<CollectionEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _mqttClient = mqttClient;
        _lifecycle = lifecycle;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var collector =
                        scope.ServiceProvider.GetRequiredService<IDeviceCollector>();
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

        await _mqttClient.DisconnectAsync();
        _lifecycle.MarkStopped();
        await base.StopAsync(cancellationToken);
    }
}
