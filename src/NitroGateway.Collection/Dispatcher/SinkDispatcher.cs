using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Events;

namespace NitroGateway.Collection;

/// <summary>
/// 事件分发器。BackgroundService 消费 Channel，遍历 IPointStoredSink。
/// </summary>
public sealed class SinkDispatcher : BackgroundService
{
    private readonly Channel<PointStoredEvent> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SinkDispatcher> _logger;

    public SinkDispatcher(IServiceScopeFactory scopeFactory, ILogger<SinkDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _channel = Channel.CreateBounded<PointStoredEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void Post(PointStoredEvent e)
    {
        _logger.LogDebug("Post Event: Device={DeviceId}", e.DeviceId);
        if (!_channel.Writer.TryWrite(e))
            _logger.LogWarning("事件通道已满，丢弃事件: Device={DeviceId}", e.DeviceId);
    }

    public override void Dispose()
    {
        _channel.Writer.TryComplete();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                while (_channel.Reader.TryRead(out var e))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var sinks = scope.ServiceProvider.GetServices<IPointStoredSink>().ToList();
                        _logger.LogDebug("SinkDispatcher: 找到 {Count} 个 Sink", sinks.Count);
                        foreach (var sink in sinks)
                        {
                            try 
                            { 
                                _logger.LogDebug("Sink={Type}", sink.GetType().Name);
                                await sink.OnStoredAsync(e); 
                            }
                            catch (Exception ex) 
                            { 
                                _logger.LogError(ex, "Sink 异常: {SinkType}", sink.GetType().Name); 
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "消费事件异常");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        _logger.LogInformation("SinkDispatcher 已停止。");
    }
}
