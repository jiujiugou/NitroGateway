using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Forwarder;

/// <summary>数据转发实现</summary>
public sealed class Forwarder : IForwarder
{
    private readonly IForwardBuffer _buffer;
    private readonly IMessageSerializer _serializer;
    private readonly IMqttClient _mqtt;
    private readonly ILogger<Forwarder> _logger;

    /// <summary>创建转发器</summary>
    public Forwarder(
        IForwardBuffer buffer,
        IMessageSerializer serializer,
        IMqttClient mqtt,
        ILogger<Forwarder> logger)
    {
        _buffer = buffer;
        _serializer = serializer;
        _mqtt = mqtt;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct = default)
    {
        // 1. Dequeue
        var dequeueResult = await _buffer.DequeueAsync(maxCount, ct);
        if (dequeueResult.IsFailure || dequeueResult.Value!.Count == 0)
            return OperationResult.Success();

        var committed = new List<Guid>();

        foreach (var batch in dequeueResult.Value!)
        {
            try
            {
                // 2. Serialize
                var payload = _serializer.Serialize(batch);

                // 3. Send
                var topic = $"nitrogateway/{batch.DeviceId}/measurements";
                var result = await _mqtt.PublishAsync(topic, payload, qos: 1, ct);

                if (result.IsSuccess)
                    committed.Add(batch.Id);       // 4. 标记待 Commit
                else
                    _logger.LogWarning("转发失败 {BatchId}: {Error}", batch.Id, result.Error!.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发异常 {BatchId}", batch.Id);
            }
        }

        // 5. Commit 成功的
        if (committed.Count > 0)
            await _buffer.CommitAsync(committed, ct);

        return OperationResult.Success();
    }
}
