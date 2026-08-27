using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Command;

/// <summary>
/// 命令订阅后台服务（ADR-069）：订阅 <c>nitrogateway/+/+/commands</c>（QoS1），
/// 逐条 解析 → 处理 → 回执。与 MqttHostedService 共用同一 <see cref="IMqttClient"/> 单例，
/// 订阅由 wrapper 在重连后自动重放（ADR-006 P1-2），本服务只在首次 Connected 时订阅一次。
/// 消息按序同步处理（命令量小；顺序保证写序与幂等）。
/// </summary>
public sealed class CommandHostedService : BackgroundService
{
    /// <summary>下行命令订阅通配（与云侧契约一致：nitrogateway/{siteId}/{deviceId}/commands）</summary>
    public const string CommandsSubscription = "nitrogateway/+/+/commands";

    /// <summary>命令 topic 后缀（快速过滤非命令消息）</summary>
    private const string CommandTopicSuffix = "/commands";

    private readonly IMqttClient _mqtt;
    private readonly CommandProcessor _processor;
    private readonly IConfiguration _config;
    private readonly ILogger<CommandHostedService> _logger;

    /// <summary>是否已成功订阅（0=未订阅，1=已订阅）；wrapper 重连自动重放，无需重复订阅</summary>
    private int _subscribed;

    public CommandHostedService(
        IMqttClient mqtt,
        CommandProcessor processor,
        IConfiguration config,
        ILogger<CommandHostedService> logger)
    {
        _mqtt = mqtt;
        _processor = processor;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _mqtt.StateChanged += OnStateChanged;
        try
        {
            // 启动时若已连接（wrapper 先完成首连），立即订阅一次
            if (_mqtt.State == MqttConnectionState.Connected)
                await SubscribeOnceAsync(ct);

            await foreach (var msg in _mqtt.Messages.WithCancellation(ct))
            {
                if (!msg.Topic.EndsWith(CommandTopicSuffix, StringComparison.Ordinal))
                    continue;

                try
                {
                    await ProcessMessageAsync(msg, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单条消息异常不中断消费循环——坏命令只记日志，后续命令继续处理
                    _logger.LogError(ex, "命令处理异常: Topic={Topic}", msg.Topic);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停机取消：正常退出
        }
        finally
        {
            _mqtt.StateChanged -= OnStateChanged;
        }
    }

    /// <summary>连接建立（含重连成功）时订阅一次；失败记录日志，等待下次 Connected 重试</summary>
    private void OnStateChanged(MqttConnectionState state)
    {
        if (state == MqttConnectionState.Connected)
            _ = SubscribeOnceAsync();
    }

    /// <summary>首次成功订阅后置位；并发 Connected 事件由 CompareExchange 去重（IMqttClient 契约不抛异常）</summary>
    private async Task SubscribeOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _subscribed, 1, 0) != 0)
            return;

        var r = await _mqtt.SubscribeAsync(CommandsSubscription, qos: 1, ct);
        if (r.IsFailure)
        {
            Interlocked.Exchange(ref _subscribed, 0);
            _logger.LogWarning("命令订阅失败: {Error}", r.Error?.Message);
        }
    }

    /// <summary>解析并处理一条 MQTT 消息；非法命令仅记 Debug 跳过</summary>
    private async Task ProcessMessageAsync(MqttMessage msg, CancellationToken ct)
    {
        // Site:Id 惰性解析：Program 在 InitializeDatabase 后把真实值写回配置，
        // 构造期读不到，故每条命令实时解析（缺省回退 "default"）。
        var localSiteId = SiteOptions.Resolve(_config[SiteOptions.IdKey]);
        var parse = CommandRequestParser.Parse(msg.Topic, msg.Payload, localSiteId);
        if (parse.IsFailure)
        {
            _logger.LogDebug("忽略非法命令: Topic={Topic} Error={Error}", msg.Topic, parse.Error?.Message);
            return;
        }

        await _processor.ProcessAsync(parse.Value!, ct);
    }
}
