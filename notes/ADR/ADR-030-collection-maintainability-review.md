# ADR-030: Collection 模块可维护性 Review（二轮，2026-08-11）

- 日期: 2026-08-11 | 状态: 待处理
- 背景: ADR-016（2026-08-09 全量 review，10 条已闭环）后第二轮可维护性扫描；本轮无 P1 级问题
- 验证: 纯 review 无代码改动，未跑测试

## 条目

- P2-1 `DataDispatcher.DispatchAsync` 返回值形同虚设：恒返回 Success（DataDispatcher.cs:106），Measurement channel 满 / Buffer 入队失败仅记日志；唯一调用方忽略结果（DeviceCollector.cs:119）→ 转发入队失败被掩盖（熔断 RecordSuccess、CollectionTotal 计 success、健康判定不受影响），且与 Dispatcher/DESIGN.md「任一失败返回 Error」矛盾。方向：返回值如实反映失败并让调用方使用（失败→RecordFailure/指标），或明确「仅入队」语义、简化接口并同步文档。
- P2-2 子模块 DESIGN 文档漂移未标注：Dispatcher/DESIGN.md（仍写 IMeasurementStore 直写、返回 Error）、DeviceReader/DESIGN.md（仍写 IProtocolDriverFactory + 每轮 Connect/Disconnect）与实现（MeasurementWriteHost Channel 异步落库、IProtocolDriverPool 长连接）不符；root DESIGN.md 与 Pipeline/DESIGN.md 已按 ADR-016 P3-6 标注历史，这两个漏标。方向：同 P3-6 标注历史快照 + 列漂移点，或删除。
- P2-3 熔断器注册表与死区缓存无清理：CircuitBreakerRegistry._map（CircuitBreakerRegistry.cs:11，接口无 Remove）、PointValuePipeline._lastValues（PointValuePipeline.cs:17）随设备/点位删除无限增长（对比 DeviceHealthMonitor.Remove 已做清理，DeviceManager.cs:60）。方向：新增 Remove 并在设备/点位删除路径调用，或加容量上限，或文档化「重启清空」语义。
- P3-1 CircuitBreaker 用 DateTime.UtcNow 墙钟做冷却/探测超时（CircuitBreaker.cs:30,32,94,128,152）：时钟回拨卡死恢复探测、前跳提前放行。方向：换单调时钟（Environment.TickCount64/Stopwatch）。
- P3-2 CollectionEngine 跨线程共享字段无同步：_roundCts/_currentRound（CollectionEngine.cs:20-22,60-61,104）ExecuteAsync 写、StopAsync 读；赋值前读到 null 会跳过「等本轮结束」直接 MarkStopped（ADR-016 P1-1 协调在竞态窗口退化为取消）。方向：Interlocked 交接或专用信号。
- P3-3 依赖方向倒置 Collection→Host：CollectionEngine.cs:5 依赖 NitroGateway.Host.GatewayLifecycle，而 Host csproj 零 ProjectReference（纯壳）；库模块依赖宿主模块，未来 Host 引用 Collection 即循环。方向：生命周期状态接口下沉 Shared/Domain。
- P3-4 通道解析逻辑重复：CollectionServiceCollectionExtensions.ResolveForwardChannels（:80-90）与 ForwarderOption.cs:31-38 同实现同文案，注释已写「保持一致」提示漂移风险。方向：抽 Shared 公共解析复用。
- P3-5 RecordFailure 调用点语义隐晦：DeviceCollector.cs:90 读取失败即调 RecordFailure，但 CircuitBreaker.RecordFailure（:117-132）非 HalfOpen 直接 return，Closed 下真正熔断由 HealthMonitor Offline→Trip 驱动。方向：注释说明职责边界，或收敛单一入口。
- P3-6 零碎一致性问题：CollectionOption.IntervalMs 为 set、其余 init（:10 vs 13/16/19）；PointValuePipeline.cs:113-117 空 if+else 宜反转为 if(!inDeadband)；MeasurementWriteHost.cs:19 与 SinkDispatcher.cs:16 DrainTimeout 重复；DataDispatcher.cs:77/MeasurementWriteHost.cs:46 格式不一致且仓库无 .editorconfig；子目录与扁平命名空间 NitroGateway.Collection 不一致；HealthReporter.cs:25 catch{} 静默无日志。方向：逐一小修 + 决定是否加 .editorconfig。
