# 08 · 有界队列 + 消费循环 + 停机排空原语（只统一原语）

- 类别：原语统一
- 优先级：中
- 状态：待办

## 现状（多套队列/消费者/排空）

| 位置 | 文件 | 形态 |
|---|---|---|
| 采集分发 | `src/NitroGateway.Collection/Dispatcher/SinkDispatcher.cs` | 有界 `Channel` + 停机排空 |
| 采集写入 | `src/NitroGateway.Collection/Dispatcher/MeasurementWriteHost.cs` | `Channel` + 消费循环 |
| 告警 | `src/NitroGateway.Alarm/Hosted/AlarmHostedService.cs` | `ConcurrentQueue` + `SemaphoreSlim` 信号 |
| MQTT 入站 | `src/NitroGateway.Transport/MQTT/MqttClientWrapper.cs` | 有界 `Channel`（容量 10000） |
| SignalR | `src/NitroGateway.Webapi/Hubs/OutboxConsumer.cs`、`DeviceStatusDispatcher.cs` | `Channel` + 消费 |

同为"有界队列 + 后台消费 + 关停排空 + 背压"，各写一遍且行为细节不一。

## 只统一这个原语

`BoundedConsumer<T>`：
- 机制：有界 `Channel`、后台消费循环、停机 `drain`（停止写入 → 排空 → 退出）、背压策略（Wait/Drop）可配。
- 策略：容量、满时行为、批量大小、drain 超时（参数）。

## 为什么 DB outbox 不并入

`SqliteForwardOutbox` 有**耐久性**要求（Pending→InFlight→Commit/死信），是磁盘背书写路径，与内存队列不是一类。

## 涉及改动文件（待办）

- [ ] 新增 `BoundedConsumer<T>`（通用机制层）
- [ ] 上述内存队列逐个迁移（保留各自 T 与消费逻辑）
- [ ] **不**改 `SqliteForwardOutbox` / Forwarder 持久化路径

## 验收 / 测试

- 各模块现有队列/排空相关单测保持绿。
- 新增 `BoundedConsumer` 单测：满时背压（不丢不撑爆）、停机排空不丢已入队项、消费异常隔离。

## 备注

关停排空与 [Host 生命周期] 相关（`CollectionEngine.StopAsync`、Forwarder drain）；顺序策略保留在编排层，不并入本原语。
