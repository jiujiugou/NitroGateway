using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Events;

namespace NitroGateway.Collection;

/// <summary>
/// 事件分发器（BackgroundService）。消费有界 Channel，为每个 <see cref="PointStoredEvent"/>
/// 创建独立 DI scope 并遍历所有 <see cref="IPointStoredSink"/> 异步推送。
/// <para><b>边界：</b>Channel 容量 1000 条，满时丢弃最旧事件并记录警告；
/// 单个 Sink 异常只记录日志，不影响其他 Sink 与后续事件；停止时先排空剩余事件（限时 5s，ADR-016 P2-3）。</para>
/// </summary>
public sealed class SinkDispatcher : BackgroundService
{
    /// <summary>停机排空时间上限：防止慢 Sink 把停机拖死。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>有界事件 Channel：容量 1000 条，满时丢弃最旧。</summary>
    private readonly Channel<PointStoredEvent> _channel;
    /// <summary>scope 工厂；每个事件创建独立 scope，保证 Sink 的 Scoped 依赖隔离。</summary>
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SinkDispatcher> _logger;

    /// <summary>创建事件分发器。</summary>
    /// <param name="scopeFactory">DI scope 工厂</param>
    /// <param name="logger">日志记录器</param>
    public SinkDispatcher(IServiceScopeFactory scopeFactory, ILogger<SinkDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _channel = Channel.CreateBounded<PointStoredEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>
    /// 非阻塞投递一个存储事件到 Channel。
    /// </summary>
    /// <param name="e">存储事件（含设备 ID 与快照）</param>
    public void Post(PointStoredEvent e)
    {
        _logger.LogDebug("Post Event: Device={DeviceId}", e.DeviceId);
        if (!_channel.Writer.TryWrite(e))
            _logger.LogWarning("事件通道已满，丢弃事件: Device={DeviceId}", e.DeviceId);
    }

    /// <summary>
    /// 释放资源：完成 Channel（不再接受新事件），后台消费完剩余事件后退出。
    /// </summary>
    public override void Dispose()
    {
        _channel.Writer.TryComplete();
        base.Dispose();
    }

    /// <summary>
    /// 后台消费循环：逐条取出事件，创建 scope 并依次调用所有 Sink；停止时排空剩余事件。
    /// </summary>
    /// <param name="ct">取消令牌；取消时停止消费（正常关闭）</param>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                while (_channel.Reader.TryRead(out var e))
                {
                    await ProcessEventAsync(e);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }

        // ADR-016 P2-3：停机排空——剩余事件尽量送达（限时），避免"取消即丢"与注释不符
        var drainDeadline = DateTime.UtcNow + DrainTimeout;
        while (_channel.Reader.TryRead(out var e))
        {
            if (DateTime.UtcNow > drainDeadline)
            {
                _logger.LogWarning("停机排空超时，剩余事件丢弃");
                break;
            }
            await ProcessEventAsync(e);
        }
        _logger.LogInformation("SinkDispatcher 已停止。");
    }

    /// <summary>逐条处理事件：创建 scope 并依次调用所有 Sink；单个 Sink 异常不影响其余。</summary>
    private async Task ProcessEventAsync(PointStoredEvent e)
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
