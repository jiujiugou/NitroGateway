# Telemetry 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Telemetry`（可观测性模块：Prometheus 指标 + Activity 追踪）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来、讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/失败路径/并发）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
模块骨架（4 个源文件 + csproj）→ NitroMetrics 9 个指标逐一定位上报点 →
指标类型选型（Counter/Gauge/Histogram）→ 陷阱题（哑火指标/help 文本/文档漂移）→
Tracing 三件套（Source / Activities / Tags）→ Activity 状态约定（Ok/Error/Unset）→
监听者机制（为什么生产没数据、测试怎么捕获）→ 链路与异步上下文 → 诊断与开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 指标定义 | `src/NitroGateway.Telemetry/NitroMetrics.cs` | 全局 9 个 Prometheus 指标静态字段，命名 `nitro_{领域}_{指标}_{单位后缀}` |
| DI 注册 | `src/NitroGateway.Telemetry/TelemetryServiceCollectionExtensions.cs` | `AddNitroTelemetry` 空实现（prometheus-net 静态注册表，无需 DI） |
| Span 名称 | `src/NitroGateway.Telemetry/Tracing/GatewayActivities.cs` | 8 个统一 Activity 名常量，禁止业务代码写字符串 |
| ActivitySource | `src/NitroGateway.Telemetry/Tracing/GatewayActivitySource.cs` | 全局唯一 ActivitySource "NitroGateway" |
| Tag 常量 | `src/NitroGateway.Telemetry/Tracing/GatewayActivityTags.cs` | 9 个统一 Tag Key |
| 项目文件 | `src/NitroGateway.Telemetry/NitroGateway.Telemetry.csproj` | prometheus-net ×2 + OpenTelemetry ×2（重复引用） |
| /metrics 端点 | `src/NitroGateway.Webapi/Program.cs:116` | `app.MapMetrics()` 暴露 Prometheus 文本格式 |

## 指标上报点全景（9 个指标 → 谁在写）

| 指标 | 上报点 |
| --- | --- |
| nitro_collection_total | `DeviceCollector.cs:90,129`（failure / success） |
| nitro_collection_duration_ms | **无上报点**（预留未接线，ADR-009 P1-1） |
| nitro_circuit_breaker_state | `DeviceCollector.cs:91,130` |
| nitro_forward_total | `Forwarder.cs:109,116,126`（deadletter 标签无上报） |
| nitro_buffer_backlog | `Forwarder.cs:146` |
| nitro_throttle_batch_size | `Forwarder.cs:147` |
| nitro_mqtt_state | `MqttClientWrapper.cs:265`（SetState） |
| nitro_devices_online | **无上报点**（预留未接线，ADR-009 P1-2） |
| nitro_devices_available | `DeviceCollector.cs:153` |

## 跨模块依赖（答题时需要的上下文）

- 指标写入方：Collection（DeviceCollector）、Forwarder（Forwarder）、Transport.MQTT（MqttClientWrapper）直接引用 `NitroMetrics` 静态字段
- Activity 写入方：Collection（CollectRound / CollectDevice / ReadDevice / Pipeline / Dispatch）、Forwarder（Forward）、Persistence.Sqlite（SqliteWrite）、Transport.MQTT（MqttPublish）
- 时序写入实际由 `MeasurementWriteHost`（Collection/Dispatcher 的 BackgroundService）消费 Channel 后调用 `SqliteMeasurementStore.WriteAsync` → `SqliteWrite` span 的父上下文是消费者线程，不是 `Dispatch`
- `/metrics` 由 Webapi 暴露；Telemetry 模块自身无 HTTP 端点
- 追踪现状：仓库（除测试外）无 OpenTelemetry SDK / ActivityListener 注册 → 生产环境 `StartActivity` 返回 null，追踪实际未启用；测试用 `ActivityListener` 捕获（`ForwarderActivityTests`）

## 注意事项

- **代码是唯一事实来源**：`docs/03-功能清单.md` F-23 写「8 个指标」，实际 9 个字段（文档漂移）；F-25 写「8 个 Span」与 `GatewayActivities` 一致
- 已知缺口已登记 `notes/ADR/ADR-009-telemetry-observability-gaps.md`：duration/online 哑火、deadletter 标签未用、mqtt_state help 顺序错、F-23 漂移、csproj 重复引用
- 测试参照：`tests/NitroGateway.UnitTests/ForwarderActivityTests.cs`（ActivityListener 捕获 + 状态断言）；当前无指标单测
- 答完全部题目后，试着不看代码画出「采集一轮 → SQLite/MQTT」的完整指标与 span 时间线
