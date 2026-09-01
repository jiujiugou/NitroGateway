# ADR-028: Desktop 与其他模块联动决策

- 日期: 2026-08-10 | 状态: 已实施
- 来源: 核查 src/NitroGateway.Desktop 与 Collection/Forwarder/MQTT/Device/Alarm/Persistence/Host 的事件、DI、生命周期联动（ADR-026/027 之后）

## Context

联动核查确认 EventBridge 三通道（SinkDispatcher/HealthListenerRegistrar/MqttClientWrapper）与注册顺序均正确（Forwarder 先于 Collection，停机顺序：CollectionEngine drain → SinkDispatcher 5s 排空 → ForwarderEngine 停机排空）。残留三个问题：CI 与 Windows 桌面工程联动断裂（并行 slnx 下 ForwarderEngineTests 停机排空 ~50% 失败）；告警上行契约外流量；EventBridge 帧循环异常后永久停摆。

## Decision

- D1 CI 拆双 job：build-server(ubuntu) 构建 Webapi/Ingest/IntegrationTests 并跑集成测试；build-windows(windows-latest) 全量构建 slnx + 顺序跑 UnitTests→IntegrationTests。连带 ForwarderEngine 停机排空 flaky 修复：根因是 .NET 10 BackgroundService.StartAsync 改用 Task.Run 调度 ExecuteAsync，StopAsync 取消若先于委托启动则 Task.Run 直接返回 Canceled、排空不发生；ForwarderEngine.ExecuteAsync 入口加确定性启动日志，测试等该日志再注入停机现场。
- D2 告警上行契约：Ingest 订阅 `nitrogateway/+/alarms`（QoS1）→ 按 AlarmId UPSERT 中心 alarms（状态迁移覆盖）；现场侧 MqttAlarmNotifier 不变。
- D3 EventBridge 帧循环改为 while + 重建 PeriodicTimer 重试（200ms 延迟），异常后自动恢复不再永久停摆。

## Alternatives

- 单一 job 全量串行：简单但平台覆盖不足、慢。
- 告警另开独立上行通道：契约外、中心接收侧需多维护一条链路。

## Rationale

桌面工程必须在 Windows 平台构建测试、服务器侧在 ubuntu 验证，双 job 各司其职；告警上行统一走 alarms topic 复用现有链路；帧循环自愈避免单点异常后整站数据停摆。

## Consequences

- CI 覆盖 Windows 桌面工程与服务器侧集成；并行构建不再出现停机排空 flaky。
- 告警 Active→Resolved 生命周期在中心完整；EventBridge 异常自动恢复。
