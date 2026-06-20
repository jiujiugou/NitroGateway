# Forwarder 模块设计文档

## 定位

数据转发。从 Buffer 取批量数据 → 序列化 → 通过 MQTT 发到云端 → 确认后删除。
五步流水线，v1-v5 骨架不变，只换各步实现。

---

## 流水线（版本无关）

```
IForwardBuffer   IMessageSerializer    IMqttClient    IForwardBuffer    Buffer 语义
     │                  │                   │               │               │
     │── Dequeue(N) ──→  │                   │               │               │
     │                  │                   │               │               │
     │              Serialize(bytes)         │               │               │
     │                  │                   │               │               │
     │                  │── PublishAsync ──→  │               │               │
     │                  │                   │               │               │
     │                  │           ← Success ── Commit ──→  │               │
     │                  │           ← Fail    ── skip ────→  │  (不 Commit)  │
     │                  │                   │               │               │
     │             下轮 Scheduler 触发，失败的自然重出                            │
```

---

## 接口

### IForwarder

```csharp
Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct);
```

由 Scheduler 定时调用（如每 5 秒）。内部编排五步。

### IMessageSerializer

```csharp
public interface IMessageSerializer
{
    byte[] Serialize(BatchMeasurements batch);
    string ContentType { get; }
}
```

---

## v1 决策

| 步骤 | v1 | v2+ |
|---|---|---|
| Dequeue | 全量（maxCount = int.MaxValue），SQLite Buffer | 按设备分组/批量限制 |
| Serialize | JSON（System.Text.Json），整批复用 | Protobuf + 压缩 |
| Topic | `nitrogateway/{deviceId}/measurements` | 多级 Topic / 按点级别 |
| QoS | QoS 1（至少一次） | 按消息类型选 QoS |
| Commit | PublishAsync 成功即 Commit | 云端 ACK 确认后才 Commit |
| Retry | Buffer 两阶段（失败不 Commit，下轮自然重出） | 死信队列 + 自适应退避 |
| Channel | 只 MQTT | MQTT + HTTP 双通道 |

---

## 实现

```csharp
public sealed class Forwarder : IForwarder
{
    private readonly IForwardBuffer _buffer;
    private readonly IMessageSerializer _serializer;
    private readonly IMqttClient _mqtt;
    private readonly ILogger<Forwarder> _logger;

    public async Task<OperationResult> ForwardBatchAsync(int maxCount, CancellationToken ct)
    {
        // 1. Dequeue
        var dequeueResult = await _buffer.DequeueAsync(maxCount, ct);
        if (dequeueResult.IsFailure || dequeueResult.Value!.Count == 0)
            return OperationResult.Success();

        var committed = new List<Guid>();
        foreach (var batch in dequeueResult.Value!)
        {
            // 2. Serialize
            var payload = _serializer.Serialize(batch);

            // 3. Send
            var topic = $"nitrogateway/{batch.DeviceId}/measurements";
            var result = await _mqtt.PublishAsync(topic, payload, qos: 1, ct);

            if (result.IsSuccess)
                committed.Add(batch.Id);   // 4. 标记待 Commit
            else
                _logger.LogWarning("转发失败 {BatchId}: {Error}", batch.Id, result.Error!.Message);
        }

        // 5. Commit 成功的
        if (committed.Count > 0)
            await _buffer.CommitAsync(committed, ct);

        return OperationResult.Success();
    }
}
```

---

## DI

```csharp
services.AddNitroForwarder(intervalMs: 5000);
// 注册 IForwarder + JsonMessageSerializer
// 注册到 Scheduler："forward" 任务每 5s 执行
```

---

## 演进

| v1 | JSON + MQTT QoS1 + Buffer 自然重试 | 当前 |
| v2 | Protobuf + 多 Topic | 数据量大时 |
| v3 | 双通道（MQTT+HTTP）+ 死信队列 | 网络环境复杂 |
| v4 | 应用层 ACK + 自适应退避 | 需要确认消费 |
| v5 | 批量合并 + 压缩 + 蜂窝网络优化 | 带宽敏感 |
