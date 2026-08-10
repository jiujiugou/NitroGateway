# ADR-028: Desktop 模块与其他模块联动 review（2026-08-10）

- 日期: 2026-08-10 | 状态: P1-1/P2-1 已修复（2026-08-10），P3-1 待修复 | 来源: 核查 src/NitroGateway.Desktop 与 Collection/Forwarder/MQTT/Device/Alarm/Persistence/Host 的事件、DI、生命周期联动（ADR-026/027 之后）
- 范围: 桌面注册顺序（GatewayHost）、EventBridge 三通道、关闭 drain 顺序、Alarm 上行、CI 与 TFM

## 已确认正确的联动（不复述细节）
- EventBridge 经 SinkDispatcher/HealthListenerRegistrar/MqttClientWrapper 接入 IPointStoredSink、IDeviceHealthListener、IMqttStateListener 三通道，与 Webapi 的 SignalR 接线同一套机制
- 注册顺序对齐 Webapi（Forwarder 先于 Collection）→ 停机顺序正确：CollectionEngine drain → SinkDispatcher 5s 排空 → ForwarderEngine 停机排空（MQTT 单例仍连接）
- AddNitroProtocol 已含 Modbus/S7 工厂注册；DesktopPathConfig 先于 AddNitroSqlite 生效；369 测试通过

## 问题
- P1-1 CI 与 Windows 桌面工程联动断裂（已修复 2026-08-10，条目已清）: `.github/workflows/ci.yml` 拆双 job——build-server(ubuntu) 构建 Webapi/Ingest/IntegrationTests 并跑集成测试；build-windows(windows-latest) 全量构建 slnx + 顺序跑 UnitTests→IntegrationTests。连带修复 ForwarderEngineTests 停机排空 flaky（并行 slnx 下 ~50% 失败，CI 顺序化前即暴露）: 根因是 .NET 10 BackgroundService.StartAsync 改用 Task.Run 调度 ExecuteAsync，StopAsync 取消若先于委托启动，Task.Run 直接返回 Canceled 任务且引擎体从未运行、排空不发生（DIAG 实证 ExecuteTask=[Canceled] logs=[]）。修复为 ForwarderEngine.ExecuteAsync 入口加 "ForwarderEngine Started." 日志作确定性启动信号，测试改等该日志再注入停机现场（ForwarderEngine.cs:106、ForwarderEngineTests.cs:175）。验证: 并行 slnx 12 轮全绿 + 顺序 CI 等价命令全绿
- P2-1 告警上行契约外流量（已修复 2026-08-10，条目已清）: 契约确定为 Ingest 订阅 `nitrogateway/+/alarms`（QoS1）→ 按 AlarmId UPSERT 中心 alarms（状态迁移覆盖）；现场侧 MqttAlarmNotifier 不变。实现见 src/NitroGateway.Ingest（IngestService.ProcessAlarmAsync + SqliteIngestStore.UpsertAlarmAsync），IngestServiceTests 覆盖 Active→Resolved 生命周期
- P3-1 EventBridge 帧循环异常后永久停摆: LoopAsync 捕获非 OCE 异常仅记日志即退出循环，UI 数据静止无恢复。修复方向: 帧循环异常后重置 PeriodicTimer 重启循环，或置 Failure 状态供 UI 提示
