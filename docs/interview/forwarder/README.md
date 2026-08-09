# Forwarder 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Forwarder`（数据转发模块：本地缓冲 → MQTT 云端上行）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/失败路径/并发）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
Forwarder（核心循环/失败路径）→ ForwardingThrottle（AIMD 节流）
→ IForwardBuffer + SqliteForwardBuffer（两阶段状态机/死信）
→ ForwarderEngine（定时调度/积压告警/停机）
→ JsonMessageSerializer（序列化）→ IMqttClient（Transport 依赖）
→ 失败路径测试（ForwarderFailureTests）→ 遥测（Activity/指标）→ 开放题（DESIGN.md 演进）
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 转发器 | `src/NitroGateway.Forwarder/Forwarder.cs` | Dequeue→Serialize→Publish(QoS1)→Commit 核心循环，内嵌节流反馈与失败路径 |
| 节流器 | `src/NitroGateway.Forwarder/ForwardingThrottle.cs` | AIMD 自适应批量/延迟，防止 MQTT 恢复瞬间冲垮 Broker |
| 引擎 | `src/NitroGateway.Forwarder/ForwarderEngine.cs` | BackgroundService + PeriodicTimer 定时触发，积压告警、单轮排水上限、优雅停机 |
| 序列化器 | `src/NitroGateway.Forwarder/JsonMessageSerializer.cs` | camelCase JSON → UTF-8 字节 |
| 接口 | `src/NitroGateway.Forwarder/IForwarder.cs`、`IMessageSerializer.cs` | 转发服务/序列化抽象，语义约定见 XML 注释 |
| DI 注册 | `src/NitroGateway.Forwarder/ForwarderServiceCollectionExtensions.cs` | Singleton 生命周期约定 + 引擎注册 |
| 缓冲接口 | `src/NitroGateway.Storage/Buffer/IForwardBuffer.cs` | 两阶段 FIFO + 死信语义（纯接口） |
| 缓冲实现 | `src/NitroGateway.Persistence/Sqlite/SqliteForwardBuffer.cs` | SQLite 两阶段提交、启动恢复、死信队列 |
| MQTT 客户端 | `src/NitroGateway.Transport/MQTT/IMqttClient.cs` | 发布统一返回 OperationResult、连接状态机 |
| 设计文档 | `src/NitroGateway.Forwarder/DESIGN.md`、`README.md` | v1 决策与 v2-v5 演进；README 明确「指令下行未实现」 |

## 跨模块依赖（答题时需要知道的上下文）

- `IForwardBuffer`：Storage.Buffer 纯接口；实现 `SqliteForwardBuffer`（每操作独立连接，ADR-001 P1-4）
- `IMqttClient`：Transport.MQTT，`MqttHostedService` 维护连接与重连；`PublishAsync` 返回 `OperationResult` 不抛异常
- 数据来源：Collection 模块 `DataDispatcher.EnqueueAsync` 写入转发缓冲（转发器只消费）
- 死信处理：`GetDeadLettersAsync` / `RetryDeadLetterAsync` / `DiscardDeadLetterAsync` 供上层 API/前端操作
- 遥测：`NitroMetrics`（Prometheus）+ `GatewayActivitySource`（Activity "Forward"）

## 注意事项

- **代码是唯一事实来源**。`DESIGN.md` 的 v1 决策表「Dequeue 全量（maxCount=int.MaxValue）」与实现（引擎 `MaxDrainPerRound=2000` + throttle 双重限制）不一致，实现示例也省略了节流与指标——答题以代码为准（Q1.6 即此类题）。
- 测试是理解行为最快的捷径：`tests/NitroGateway.UnitTests`（ForwardingThrottleTests / ForwarderActivityTests / SqliteForwardBufferTests）、`tests/NitroGateway.IntegrationTests`（ForwarderIntegrationTests / ForwarderFailureTests / ForwarderEngineTests）。
- 答完全部题目后，试着不看代码把「Broker 断线 3 小时 → 恢复 → 积压排空」的完整时序画出来（缓冲状态 × 节流状态 × 日志 × 指标）——能画出来就是吃透了。
