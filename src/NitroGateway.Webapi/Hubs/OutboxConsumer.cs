using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Webapi.Hubs;

/// <summary>Outbox 消费者。单一后台线程消费 Channel → IHubContext.SendAsync</summary>
internal sealed class OutboxConsumer : BackgroundService
{
    private readonly ChannelReader<OutboxMessage> _reader;
    private readonly IHubContext<LiveDataHub> _hub;
    private readonly ILogger<OutboxConsumer> _logger;

    public OutboxConsumer(Channel<OutboxMessage> channel, IHubContext<LiveDataHub> hub, ILogger<OutboxConsumer> logger)
    {
        _reader = channel.Reader;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _reader.WaitToReadAsync(stoppingToken))
        {
            while (_reader.TryRead(out var msg))
            {
                try
                {
                    _logger.LogInformation("Outbox 发送: Method={Method} Target={Target}",
                        msg.Method, msg.TargetType);
                    if (msg.TargetType == OutboxTarget.All)
                        await _hub.Clients.All.SendAsync(msg.Method, msg.Payload);
                    else
                        await _hub.Clients.Group(msg.GroupId).SendAsync(msg.Method, msg.Payload);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SignalR 发送失败: {Method}", msg.Method);
                }
            }
        }
    }
}
