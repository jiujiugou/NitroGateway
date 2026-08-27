using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Shared;
using NitroGateway.Telemetry;
using NitroGateway.Telemetry.Tracing;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Command;

/// <summary>
/// 命令处理器：幂等（commandId 去重）+ 调 <see cref="IWriteService"/> 写值 + 回执发布。
/// <para>消费契约：由 <see cref="CommandHostedService"/> 单消费者按序调用，天然串行；
/// 若未来引入多消费者，需对本类补充并发控制（当前不设锁，靠单消费者保证幂等）。</para>
/// </summary>
public sealed class CommandProcessor
{
    /// <summary>回执幂等缓存容量上限：超出后简单清理一部分（只影响极旧命令的重复投递）</summary>
    private const int MaxCachedAcks = 1024;

    private readonly IWriteService _write;
    private readonly IMqttClient _mqtt;
    private readonly ILogger<CommandProcessor> _logger;

    /// <summary>已处理命令的回执缓存：commandId → 回执。重复投递直接重发，不重复写值。</summary>
    private readonly ConcurrentDictionary<Guid, CommandAck> _acks = new();

    public CommandProcessor(IWriteService write, IMqttClient mqtt, ILogger<CommandProcessor> logger)
    {
        _write = write;
        _mqtt = mqtt;
        _logger = logger;
    }

    /// <summary>
    /// 处理一条命令。幂等：commandId 首次到达才写值并缓存回执；重复投递（QoS1 重投/云侧重发）
    /// 重发缓存回执、不重复写值。写值失败回执 Failure + error；回执发布失败仅记日志
    /// （云侧重试，幂等兜底不重写值）。
    /// </summary>
    public async Task ProcessAsync(GatewayCommand command, CancellationToken ct = default)
    {
        if (_acks.TryGetValue(command.CommandId, out var cached))
        {
            _logger.LogInformation("重复命令，重发缓存回执: CommandId={CommandId}", command.CommandId);
            await PublishAckAsync(command, cached, ct);
            return;
        }

        var ack = await ExecuteWriteAsync(command, ct);
        _acks.TryAdd(command.CommandId, ack);
        TrimIfNeeded();

        await PublishAckAsync(command, ack, ct);
    }

    /// <summary>执行写值并构造回执（唯一的写值点；写值异常隔离为 Failure 回执，不中断消费循环）</summary>
    private async Task<CommandAck> ExecuteWriteAsync(GatewayCommand command, CancellationToken ct)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.CommandProcess);
        activity?.SetTag(GatewayActivityTags.DeviceId, command.DeviceId.ToString());

        OperationResult result;
        try
        {
            result = await _write.WriteAsync(new WriteRequest
            {
                DeviceId = command.DeviceId,
                PointId = command.PointId,
                Value = command.Value
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"写值异常: {ex.Message}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            activity?.SetTag(GatewayActivityTags.ErrorMessage, error);
            NitroMetrics.CommandProcessedTotal.WithLabels("failure").Inc();
            _logger.LogWarning("命令写值异常: CommandId={CommandId} Device={DeviceId} Point={PointId} Error={Error}",
                command.CommandId, command.DeviceId, command.PointId, error);
            return CommandAck.Failure(error, DateTimeOffset.UtcNow);
        }

        if (result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
            NitroMetrics.CommandProcessedTotal.WithLabels("success").Inc();
            _logger.LogInformation("命令写值成功: CommandId={CommandId} Device={DeviceId} Point={PointId} Value={Value}",
                command.CommandId, command.DeviceId, command.PointId, command.Value);
            return CommandAck.Success(DateTimeOffset.UtcNow);
        }

        var errorMessage = result.Error?.Message ?? "写值失败";
        activity?.SetStatus(ActivityStatusCode.Error, errorMessage);
        activity?.SetTag(GatewayActivityTags.ErrorMessage, errorMessage);
        NitroMetrics.CommandProcessedTotal.WithLabels("failure").Inc();
        _logger.LogWarning("命令写值失败: CommandId={CommandId} Device={DeviceId} Point={PointId} Error={Error}",
            command.CommandId, command.DeviceId, command.PointId, errorMessage);
        return CommandAck.Failure(errorMessage, DateTimeOffset.UtcNow);
    }

    /// <summary>发布回执到 commands/ack topic（QoS1）；失败仅记日志 + 指标，云侧重试兜底</summary>
    private async Task PublishAckAsync(GatewayCommand command, CommandAck ack, CancellationToken ct)
    {
        try
        {
            var topic = $"nitrogateway/{command.SiteId}/{command.DeviceId}/commands/ack";
            var payload = CommandAckSerializer.Serialize(command.CommandId, ack);
            var r = await _mqtt.PublishAsync(topic, payload, qos: 1, ct);
            if (r.IsFailure)
            {
                NitroMetrics.CommandAckPublishFailuresTotal.Inc();
                _logger.LogWarning("命令回执发布失败: CommandId={CommandId} Error={Error}",
                    command.CommandId, r.Error?.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            NitroMetrics.CommandAckPublishFailuresTotal.Inc();
            _logger.LogWarning(ex, "命令回执发布异常: CommandId={CommandId}", command.CommandId);
        }
    }

    /// <summary>缓存超上限时简单清理（移除最先插入的一部分；仅影响超极旧命令的重复投递）</summary>
    private void TrimIfNeeded()
    {
        if (_acks.Count <= MaxCachedAcks)
            return;
        foreach (var key in _acks.Keys.Take(MaxCachedAcks / 2))
            _acks.TryRemove(key, out _);
    }
}
