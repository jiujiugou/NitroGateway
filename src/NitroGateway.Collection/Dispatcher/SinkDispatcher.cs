using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Events;

namespace NitroGateway.Collection;

/// <summary>
/// 事件分发器。将 PointStoredEvent 异步推送给所有 IPointStoredSink，
/// 使用有界 Channel 解耦——DataDispatcher 不等待 Sink 完成。
///
/// <para>有界 Channel（容量 1000）提供背压——Channel 满时丢弃最旧事件，
/// 防止 Alarm 消费慢时内存无限增长。</para>
///
/// <para>实现 IDisposable：Dispose 时 Complete Channel + Cancel 消费者，
/// 确保进程关闭时不阻塞。</para>
/// </summary>
public sealed class SinkDispatcher : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SinkDispatcher> _logger;
    private readonly Channel<PointStoredEvent> _channel;
    private readonly CancellationTokenSource _cts = new();

    public SinkDispatcher(IServiceScopeFactory scopeFactory, ILogger<SinkDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _channel = Channel.CreateBounded<PointStoredEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = Task.Run(() => ConsumeLoopAsync(_cts.Token));
    }

    /// <summary>将事件推入 Channel（非阻塞）。DataDispatcher 调用。</summary>
    public void Post(PointStoredEvent e)
    {
        if (!_channel.Writer.TryWrite(e))
        {
            _logger.LogWarning("事件通道已满，丢弃事件: Device={DeviceId}", e.DeviceId);
        }
    }

    /// <summary>关闭 Channel，等待消费者退出。</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
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
                        var sinks = scope.ServiceProvider.GetServices<IPointStoredSink>();
                        foreach (var sink in sinks)
                        {
                            try { await sink.OnStoredAsync(e); }
                            catch (Exception ex) { _logger.LogError(ex, "Sink 异常: {SinkType}", sink.GetType().Name); }
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
