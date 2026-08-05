# [005] Collection 模块闭环扫描（spec — 扫描结论）

- 对应待办: [D-09] Collection 模块闭环扫描与验收补全(优化, 高) — notes/backlog.md
- 状态: 扫描已完成（2026-08-05, 用户指令启动）; 缺口处理待用户拍板

## 背景（为什么做）

- 来源: 用户指令——完成 Collection 模块的闭环与验收完整
- 需求三问:
  - 为什么做: Collection 是网关采集主链路, 存在潜在运行风险与验收盲区, 需先扫描确认缺口再补全
  - 验收标准是什么: 缺口清单有证据、可执行, 修复后有测试锚定
  - 不做会怎样: 运行期存储异常可能停机、云端点位名称为空等隐患留到现场爆发

## 扫描范围

- 代码: src/NitroGateway.Collection（引擎/采集器/读取器/管道/分发/健康/熔断）
- 测试: UnitTests + IntegrationTests 中 Collection 相关
- 口径: DESIGN.md/README、docs/02-架构理解、docs/03-功能清单、notes/blueprint/baseline

## 发现: P0（运行正确性）

- P0-1 时序库写入异常会停掉整个网关: `Dispatcher/MeasurementWriteHost.cs` ExecuteAsync 无 try/catch, IMeasurementStore.WriteAsync 抛异常 → BackgroundService 默认 StopHost。需异常隔离 + 降级/重试 + 日志。
- P0-2 云端报文点位名称为空: `Dispatcher/DataDispatcher.cs` ToBatchMeasurements 写 `PointName` 为空字符串, `Forwarder/JsonMessageSerializer.cs` 整包序列化直接进 MQTT; 集成测试手工填 T1 掩盖了该问题。需快照携带点名称或分发时回填。
- P0-3 采集引擎意外异常后永久停止: `CollectionEngine.cs` 外层 catch 在 while 循环外, 异常后只留一条日志就退出, 无恢复机制。

## 发现: P1（可观测性与契约）

- P1-1 DispatchAsync 恒返回 Success: 缓冲入队失败/通道满只记日志, 与 DESIGN.md 声明任一失败返回 Error 不符, 失败信号丢失。
- P1-2 指标缺口: `NitroMetrics.CollectionDurationMs` 定义但从未上报(死指标); 无通道丢弃计数、无熔断 Trip/Reset 计数、无采集轮次计数。
- P1-3 HealthReporter 吞异常无日志, 健康上报失败不可观测。
- P1-4 死代码: `Resilience/CircuitBreakerListenerRegistrar.cs` 未注册(熔断监听实际由 Device 的 HealthListenerRegistrar 批量挂载, 功能正常); Device 侧 PersistenceListenerRegistrar 同样未注册。

## 发现: P2（文档与口径一致）

- P2-1 `Collection/DESIGN.md v1` 严重过期: 结构图缺 Collector/Resilience; DeviceReader 描述为每轮 Connect/Disconnect(实际驱动池长连接复用); Pipeline 描述含协议解码+死区丢弃(实际无解码、死区不丢弃); DataDispatcher 描述同步写 TimeSeries(实际 Channel 异步); CollectionEngine 描述串行 RunAsync(实际 BackgroundService+并发+熔断)。
- P2-2 `Pipeline/IPointValuePipeline.cs` XML 注释死区丢弃不包含在结果中, 与实现不符(数据不丢弃, 仅缓存不刷新)。
- P2-3 docs/02-架构理解.md 熔断器状态机描述 Closed→(5 连败)→Open→(30s)→HalfOpen 与代码不符(实际: Closed 下 RecordFailure 无效; Open 由 Offline 信号 Trip; 冷却 5s 起步翻倍; HalfOpen 探测 30s 超时释放)。
- P2-4 docs/03 F-10 死区过滤措辞误导(实际不过滤); F-08~F-12 只有位置无验收方式。

## 发现: P3（测试缺口, 验收完整）

- P3-1 无单测: CircuitBreakerRegistry、CircuitBreakerHealthListener(Online→Reset/Offline→Trip)、DataDispatcher(双写失败互不阻塞/缓冲失败/空快照)、MeasurementWriteHost(通道满/写异常)、SinkDispatcher(sink 异常隔离)、DeviceCollector(熔断跳过/读失败/无点位跳过)、CollectionEngine(StopAsync 排空/超时取消)、HealthReporter、DeviceReader(空点位/异常→Protocol 错误)。
- P3-2 集成测试仅 happy path: PipelineDispatchIntegrationTests 未注册任何 IPointStoredSink, 事件链路未真正验证。

## 正面确认（无需处理）

- 熔断器状态机实现与单测覆盖良好(冷却翻倍/单探测/30s 超时释放/线程安全)。
- 双写解耦、Channel 背压 DropOldest、Sink 异常隔离、健康上报不崩采集循环。
- 驱动池长连接复用 + ReliableProtocolDriver Polly 3 次重试指数退避。
- Pipeline 死区语义(不丢数据, 仅影响告警缓存)有测试锚定。

## 决策点（需用户拍板）

- [ ] 决策点 1: 缺口处理策略: A) 按严重度分批, P0 优先(推荐) / B) 全部纳入本项一次闭环 / C) 只修 P0
- [ ] 决策点 2: 空 PointName 修法: 快照携带点名称(推荐) / 分发时按 DevicePointId 回查回填 / 接受空名(不推荐)

## 工程基线（固定节, 勿删; 不适用项标 N/A）

- [ ] 错误处理: P0-1/P0-3 修复后失败路径有日志与降级
- [ ] 健康检查/可观测: P1-2 修复后采集耗时/丢弃计数可观测
- [ ] 测试: P3 缺口按修复项补单测/集成测试
- [ ] 文档: P2 文档修正后与代码一致
