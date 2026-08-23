# ADR-016: Collection 模块 Code Review 清单

- 日期: 2026-08-09 | 状态: 全部条目已处理（2026-08-09）——P1-1 / P2-1~P2-3 / P3-1~P3-6 全部修复
- 用途: 对 Collection 模块全量 code review 产出的问题清单；修复后在代码加注释并删除本条
- 验证基线: `dotnet build` 0 错误；单测 211 通过；集成测试 26 通过（2026-08-09）

## 条目

- P1-1 关闭 drain 协调未实现：`GatewayLifecycle`（src/NitroGateway.Host/GatewayLifecycle.cs）的 IsDraining/IsStopped 全仓库无人读取，`RequestStop()` 语义与命名相反（把两个标志置 false）；`CollectionEngine.StopAsync`（CollectionEngine.cs:108-132）注释声称"采集先停→转发排空"，但 HostedService 反向停止 + 注册顺序（Program.cs:58-59 Collection 先于 Forwarder）导致 ForwarderEngine 先停、Collection 后停，且 Forwarder 不在停止时排空（ForwarderEngine.cs 无 StopAsync 覆写）。数据不丢（SQLite 转发缓冲持久化，下次启动续传），但设计意图未实现、注释误导。修复方向：a) 删除 GatewayLifecycle 与相关注释；或 b) 真接协调：StopAsync 起始 MarkDraining、Forwarder 检查 IsDraining 排空至空/超时、调整注册顺序。
- P2-1 采集热路径日志刷屏：`DeviceCollector.cs:73,100,104,109,122,143,162,187` 每设备每轮 6+ 条 LogInformation（含值明细），1s 间隔 × N 设备 ≈ 5N 行/秒。修复方向：每设备明细降 LogDebug；失败保留 Warning；或按 N 轮聚合。
- P2-2 CollectionOption 无校验：`AddNitroCollection`（CollectionServiceCollectionExtensions.cs:25-31）手动绑定 Options 不走验证管线；`IntervalMs<=0` 时 PeriodicTimer 启动即抛，`MaxConcurrency<=0` 时 SemaphoreSlim(0) 每轮永久挂起（仅靠 StopAsync 30s 超时兜底）。修复方向：OptionsBuilder + Validate，非法值启动报错并指明字段。
- P2-3 Channel 停机不排空：`MeasurementWriteHost.ExecuteAsync`（MeasurementWriteHost.cs:57-73）与 `SinkDispatcher.ExecuteAsync`（SinkDispatcher.cs:56-83）在 stoppingToken 取消时直接退出（WaitToReadAsync 抛 OCE），队列剩余批次/事件被丢弃，注释"优雅退出循环"与实际不符。影响：时序库在途批次（≤1000 批）与事件丢失（同批数据已入 SQLite 转发缓冲，云端可恢复）。修复方向：catch OCE 后先排空剩余项（带时限），或注释改为明确丢弃语义。
- P3-1 `CollectionEngine.StopAsync` 竞态：`_roundCts?.Cancel()`（CollectionEngine.cs:122）与 ExecuteAsync finally（:90-92）的 Dispose 无同步，极端交错下可能对已 Dispose 的 CTS 调 Cancel → ObjectDisposedException 破坏优雅关闭。修复方向：局部捕获 + catch ODE，或 Interlocked.Exchange 交接所有权。
- P3-2 探测名额闭环依赖"不抛异常"：`DeviceCollector.CollectDeviceAsync`（DeviceCollector.cs:72-133）在 TryEnterProbe 返回 true 后无 try/finally 保证 RecordSuccess/RecordFailure；当前各步骤实测不抛（Reader 已归类、Pipeline 逐点捕获、Dispatcher 缓冲已归类、Reporter 吞异常），但契约脆弱——未来任一步骤抛异常即泄漏探测名额 30s。修复方向：try/finally 包裹执行段，未上报则 RecordFailure。
- P3-3 失败明细丢失：`DeviceCollector.cs:126` `_reporter.Report(device.Id, goodCount, failCount, null)` 恒传 null，HealthMonitor 的 LastError 只能见"采集失败"占位。修复方向：取首个非 Good 快照的 ErrorMessage 传入。
- P3-4 批次扫描时间失真：`DataDispatcher.ToBatchMeasurements`（DataDispatcher.cs:97-105）把 ScanStartedAt/ScanCompletedAt 都填成分发时刻 DateTime.UtcNow，不反映真实扫描窗口。修复方向：从快照 Timestamp 取 min/max，或注释明确语义。
- P3-5 HalfOpen 探测超时并发放行：`CircuitBreaker.TryEnterProbe`（CircuitBreaker.cs:80-81）探测卡住 30s 后直接释放并放行新探测，旧探测可能仍在途（TCP 超时>30s），短暂出现两个并发探测，与"仅放行第一个"注释不符。修复方向：基于探测 Task 完成而非墙钟，或文档化放宽。
- P3-6 文档漂移：模块 `DESIGN.md` 仍是 v1（`Process(IReadOnlyList<RawPointValue>)` 无 deviceId、死区"丢弃"语义），与实现（Process 带 deviceId、死区只影响缓存不丢数据）不符。修复方向：同步 DESIGN.md 或标注"仅历史参考"。


## 处理记录（2026-08-09）

- P1-1 关闭 drain 协调真接实现：`GatewayLifecycle.RequestStop()` 语义修正为标记 draining（原实现把两个标志置 false，与命名相反），新增 `MarkStopped()`；`CollectionEngine.StopAsync` 起始调 `RequestStop()`、末轮结束后调 `MarkStopped()`；`ForwarderEngine` 在 stoppingToken 取消后执行 `DrainOnShutdownAsync()`——先等待采集侧 stopped（限时 15s），再在 MQTT 仍连接期间限时（10s）把缓冲剩余批次尽量发完，MQTT 不可用则留待下次启动续传。测试：`GatewayLifecycleTests` 3 个；`StopAsync_WithConnectedMqtt_DrainsRemainingBuffer` 重写为确定性（空缓冲+断连启动 → 注入积压+连上 → Stop）。
- P2-1 热路径日志降级：`DeviceCollector` 每设备每轮明细 LogInformation → Debug，失败保留 Warning，避免 1s×N 设备刷屏。
- P2-2 Options 校验：`AddNitroCollection` 改走标准 Options 管线（Bind + Validate + ValidateOnStart），`IntervalMs<=0`、`MaxConcurrency<=0`、`CircuitBreakerOpenSeconds<0`、`CircuitBreakerMaxOpenSeconds<CircuitBreakerOpenSeconds` 启动即报错并指明字段。测试：`CollectionOptionsWiringTests` 新增校验 Theory 3 例。
- P2-3 Channel 停机排空：`MeasurementWriteHost`、`SinkDispatcher` catch OCE 后 `TryRead` 排空剩余批次/事件（各限时 5s），注释与实现一致。测试：`ChannelDrainTests` 2 个。
- P3-1 StopAsync CTS 竞态：`CollectionEngine.StopAsync` 局部捕获 `_roundCts` 并 catch `ObjectDisposedException`，容忍 ExecuteAsync finally 恰好 Dispose 的极端交错。
- P3-2 探测名额闭环：`DeviceCollector.CollectDeviceAsync` 在 TryEnterProbe 成功后以 try/catch 兜底，任何异常路径 RecordFailure 关闭探测名额（熔断器自身异常不阻断采集）。测试：`DeviceCollectorProbeTests` 2 个。
- P3-3 失败明细：`_reporter.Report` 改传首个非 Good 快照的 ErrorMessage，HealthMonitor LastError 不再恒为占位。
- P3-4 批次扫描时间：`DataDispatcher.ToBatchMeasurements` 的 ScanStartedAt/ScanCompletedAt 取快照 Timestamp min/max，不再恒为分发时刻。测试：`DataDispatcherTests` 新增 1 个。
- P3-5 HalfOpen 超时并发放行：注释明确“探测卡住超 30s 自动释放、允许新探测进入”属有意放宽（防慢读永久阻塞恢复探测），`_probing` 超时释放语义补充说明。
- P3-6 文档漂移：`src/NitroGateway.Collection/DESIGN.md` 头部标注“历史快照（ADR-016 P3-6）”，列明三处已知漂移（Process 带 deviceId、死区只影响缓存不丢数据、Reader 复用长连接）。

## 验证
- `dotnet build` 0 错误；UnitTests 211 通过（较 ADR-009 后基线 199 净增 12）；IntegrationTests 26 通过
- 新增测试文件：GatewayLifecycleTests / ChannelDrainTests / DeviceCollectorProbeTests / CollectionOptionsWiringTests（校验 Theory）/ DataDispatcherTests 批次时间戳
- 集成测试 `StopAsync_WithConnectedMqtt_DrainsRemainingBuffer` 曾偶发失败，已重写为确定性场景（等待 ExecuteTask 进入挂起态再注入停机现场），连跑 5 轮稳定
- 未提交：git 提交由用户执行