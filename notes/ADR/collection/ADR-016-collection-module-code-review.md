# ADR-016: Collection 模块 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施

## Context

Collection 模块全量 code review 发现：关闭 drain 协调未实现、热路径日志刷屏、Options 无校验、Channel 停机不排空、StopAsync 竞态、探测名额闭环依赖"不抛异常"、失败明细丢失、批次扫描时间失真、HalfOpen 探测超时并发放行、文档漂移。

## Decision

- D1 关闭 drain 协调：GatewayLifecycle.RequestStop() 语义修正为标记 draining（原实现把两标志置 false，与命名相反），新增 MarkStopped()；CollectionEngine.StopAsync 起始 RequestStop()、末轮结束 MarkStopped()；ForwarderEngine 在 stoppingToken 取消后 DrainOnShutdownAsync()——先等采集侧 stopped（限时 15s），再在 MQTT 仍连接期间限时（10s）把缓冲剩余批次尽量发完，MQTT 不可用则留待下次启动续传。
- D2 热路径日志降级：DeviceCollector 每设备每轮明细 LogInformation → Debug，失败保留 Warning，避免 1s×N 设备刷屏。
- D3 Options 校验：AddNitroCollection 走标准 Options 管线（Bind+Validate+ValidateOnStart）；IntervalMs<=0、MaxConcurrency<=0、CircuitBreakerOpenSeconds<0、CircuitBreakerMaxOpenSeconds<OpenSeconds 启动即报错并指明字段。
- D4 Channel 停机排空：MeasurementWriteHost/SinkDispatcher catch OCE 后 TryRead 排空剩余批次/事件（各限时 5s）。
- D5 StopAsync CTS 竞态：CollectionEngine.StopAsync 局部捕获 _roundCts 并 catch ObjectDisposedException，容忍 ExecuteAsync finally 恰好 Dispose 的极端交错。
- D6 探测名额闭环：DeviceCollector.CollectDeviceAsync 在 TryEnterProbe 成功后以 try/catch 兜底，任何异常路径 RecordFailure 关闭探测名额。
- D7 失败明细：_reporter.Report 改传首个非 Good 快照的 ErrorMessage，HealthMonitor LastError 不再恒为占位。
- D8 批次扫描时间：DataDispatcher.ToBatchMeasurements 的 ScanStartedAt/ScanCompletedAt 取快照 Timestamp min/max，不再恒为分发时刻。
- D9 HalfOpen 探测超时并发放行：注释明确"探测卡住超 30s 自动释放、允许新探测进入"属有意放宽（防慢读永久阻塞恢复探测）。
- D10 文档漂移：Collection/DESIGN.md 头部标注"历史快照（ADR-016 P3-6）"，列三处已知漂移（Process 带 deviceId、死区只影响缓存不丢数据、Reader 复用长连接）。

## Alternatives

- D1 备选 a：删除 GatewayLifecycle 与相关注释（放弃 drain 协调，数据靠 SQLite 缓冲持久化兜底）——被否，设计意图应实现。

## Rationale

- drain 协调使优雅关闭真正排空缓冲、注释与实现一致；日志降级避免采集热路径刷屏；Options 校验让配置错误启动即暴露；停机排空避免在途批次/事件静默丢弃；失败明细使健康监控可诊断。

## Consequences

- 关闭时转发缓冲尽量发完、MQTT 不可用则重启续传；采集日志量大幅下降；非法配置启动即报错；停机不再静默丢批次；设计文档标注历史快照。
