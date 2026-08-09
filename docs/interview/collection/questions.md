# Collection 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 10 组 45 题；参考答案见 `answers.md`。

---

## 一、架构与调度（CollectionEngine / DI）

**Q1.1 ★** 从引擎启动到数据最终落地，完整调用链是什么？说出每个环节的组件与数据形态变化（RawPointValue → PointSnapshot → BatchMeasurements → …）。

**Q1.2 ★** 采集模块的边界：哪些事情 Collection 明确**不做**？各由哪个模块负责？

**Q1.3 ★★** CollectionEngine 用什么定时器驱动主循环？为什么不会出现「轮次堆积」？与 `Task.Delay` 循环的区别是什么？
代码定位：`CollectionEngine.cs` `ExecuteAsync` 的 `PeriodicTimer` 与类注释。

**Q1.4 ★★** 为什么每轮采集要创建独立 DI scope？列出模块内 Singleton / Scoped 服务及各自的理由。
代码定位：`CollectionServiceCollectionExtensions.cs`。

**Q1.5 ★★★** `CollectOnceAsync` 获取的是**所有设备（含 Offline）**而不是「仅 Online」。为什么？如果只取 Online 设备会发生什么？
代码定位：`DeviceCollector.CollectOnceAsync` 注释；`IDeviceManager.GetAllAsync`。

**Q1.6 ★★** 单轮异常会杀死引擎吗？`_errorRetryDelay`（默认 5s）的作用？`OperationCanceledException` 为什么单独 catch？
代码定位：`CollectionEngine.ExecuteAsync` 三个 catch；测试 `CollectionEngineTests.RoundFailure_DoesNotStopEngine_RetriesNextRound`。

---

## 二、单设备采集流水线（DeviceCollector）

**Q2.1 ★** 单台设备一轮采集按代码顺序有哪几步？每步失败分别怎么处理（跳过/上报/熔断/直接 return）？
代码定位：`DeviceCollector.CollectDeviceAsync`。

**Q2.2 ★★★** 读取失败（`readResult.IsFailure`）与「读取成功但点位质量 Uncertain」对**熔断器**和**健康上报**的影响有何不同？为什么「读成功就 RecordSuccess，即使部分点位质量差」？
代码定位：`CollectDeviceAsync` 步骤 1/4/5；`HealthReporter.Report`。

**Q2.3 ★★** 并发采集如何限流？信号量是实例字段且 DeviceCollector 是 Scoped——所以限流是「轮内」还是「全局」？如果把 DeviceCollector 改成 Singleton 会有什么后果？
代码定位：`CollectOnceAsync` 的 tasks 构造；DI 注册。

**Q2.4 ★★** 维护模式设备如何被过滤？为什么以 `HealthMonitor` 实时快照为准，而不是设备配置里的 Status？fallback 逻辑是什么？
代码定位：`CollectOnceAsync` 过滤 + `IsInMaintenance`；ADR-002 P2-2 注释。

**Q2.5 ★★** `CollectDeviceAsync` 的「不抛异常」契约如何保证？如果它意外抛异常，`CollectOnceAsync` 会发生什么（WhenAll 语义）？

---

## 三、数据读取（DeviceReader）

**Q3.1 ★★** 连接策略：DESIGN.md 写「v1 每轮连接/断开」，实际实现是什么？为什么演进？
代码定位：`DeviceReader.cs` 类注释与 `ReadDeviceAsync`；`IProtocolDriverPool.GetOrCreate`。

**Q3.2 ★** 只取 `Enabled = true` 的点位；设备无启用点位时返回空列表——这是成功还是失败？为什么这样设计？

**Q3.3 ★★** 异常如何转换？为什么 `OperationCanceledException` 要重新抛出，而不是转成 `OperationResult` 失败？
代码定位：`ReadDeviceAsync` 的 catch 块。

**Q3.4 ★★** `driver.ReadBatchAsync(points, ct)` 一次批量读 vs 逐点读的差异？DeviceReader 对「批量分组/合并连续地址」负责吗？（v2 计划是什么？）
代码定位：`DeviceReader.ReadDeviceAsync`；模块 DESIGN.md 提到的 PointBatchOptimizer。

---

## 四、值转换管道（PointValuePipeline）

**Q4.1 ★** Pipeline 的输入输出与职责边界？接口注释说「纯计算，无 IO 副作用」——唯一的可变状态是什么？
代码定位：`IPointValuePipeline.cs`、`PointValuePipeline._lastValues`。

**Q4.2 ★★★** 死区的真实语义：它**丢弃数据**吗？它实际影响什么？为什么这样设计？（本项目最容易答错的一道题）
代码定位：`PointValuePipeline.ConvertSingle` 死区分支；测试 `Deadband_SmallChange_PassesThrough_ButDoesNotRefreshCache`。

**Q4.3 ★★** 数值缩放失败会怎样？为什么保留 Uncertain 快照而不是丢弃？对下游（存储/告警/健康统计）的影响是什么？
代码定位：`ConvertSingle` 的 catch 分支。

**Q4.4 ★** Bool/String 与未知类型如何处理？为什么不做缩放和死区？数值类型白名单是哪 8 种？
代码定位：`ConvertSingle` + `IsNumericType`。

**Q4.5 ★★** `GetLastValue` / `SetLastValue` 被谁调用？`_lastValues` 缓存重启丢失的后果是什么（首个值、告警 Duration 基准）？
代码定位：`IPointValuePipeline` 接口注释；测试 `Deadband_NewPipeline_FirstValueAlwaysPasses`。

**Q4.6 ★★** 死区判定细节：首次值、`Deadband = 0`、绝对值比较——分别是什么行为？「值在死区内」时缓存保持什么？

---

## 五、数据分发与异步写入（DataDispatcher 系）

**Q5.1 ★** 「双写」指哪两写？`DispatchAsync` 的三个步骤分别是什么？它们如何互不阻塞？
代码定位：`DataDispatcher.DispatchAsync`。

**Q5.2 ★★★** `DispatchAsync` 什么情况下返回失败？代码实际返回什么？DESIGN.md 的说法（「任一失败返回 Error」）与实现一致吗？（文档漂移题）

**Q5.3 ★★** `MeasurementWriteHost`：Channel 的容量与满时策略？为什么选 `DropOldest` 而不是 `DropWrite` 或阻塞？`Post` 返回 false 时谁处理、怎么处理？
代码定位：`MeasurementWriteHost` 构造函数 + `Post`；`DataDispatcher` 的调用处。

**Q5.4 ★★** 时序 Channel 满丢弃后数据会丢吗？转发缓冲（`IForwardBuffer`）在其中扮演什么角色？整体可靠性是几级设计？

**Q5.5 ★★** `SinkDispatcher`：为什么每个事件创建独立 DI scope？单个 Sink 抛异常的影响？`Dispose` 时做了什么？
代码定位：`SinkDispatcher.ExecuteAsync` / `Dispose`。

**Q5.6 ★★** `ToBatchMeasurements` 里 `DataType` 透传的背景？（ADR-001 P1-5）如果不透传，云端解析会出什么问题？
代码定位：`DataDispatcher.ToBatchMeasurements` 注释；测试 `DataType_PropagatedToSnapshot`。

---

## 六、熔断器（CircuitBreaker 系）

**Q6.1 ★** 画出状态机：Closed / Open / HalfOpen 的全部转换条件与触发者（谁调 Trip / Reset / RecordSuccess / RecordFailure）。
代码定位：`CircuitBreaker.cs` 类注释。

**Q6.2 ★★** 设计原则：「HealthMonitor 是唯一的健康决策者，CircuitBreaker 只负责保护执行」。为什么这样拆分？各自「主张」什么？
代码定位：`CircuitBreaker.cs` 类注释；`IDeviceHealthMonitor`。

**Q6.3 ★★** 冷却退避策略：起步 5s → 探测失败翻倍 → 5min 封顶；什么时候复位？为什么封顶？
代码定位：`CircuitBreaker` Trip / RecordFailure / Reset；测试 `HalfOpen_ProbeFailure_DoublesCooldownEachTime`。

**Q6.4 ★★★** HalfOpen 如何保证只放行一个探测？`_probing` / `_probeStarted` 的作用？探测卡死 30s 自动释放的意义？为什么 `TryEnterProbe` 设计成带副作用的命令、`State` 保持纯查询（CQS）？
代码定位：`CircuitBreaker.TryEnterProbe` / `State`；测试 `HalfOpen_OnlyOneProbeAllowed` / `State_Getter_IsPureQuery_DoesNotTransitionOrConsumeProbe`。

**Q6.5 ★★★** `RecordFailure` 在 Closed 状态下被忽略——为什么？那 Closed 下连续失败 10 次熔断器会怎样？真正打开熔断器的路径有哪两条？
代码定位：`RecordFailure`；测试 `RecordFailure_InClosed_DoesNotOpen`。

**Q6.6 ★★** Registry：惰性创建、线程安全如何保证？设备删除后熔断器实例怎么处理（常驻内存）？`GetAll` 的用途？
代码定位：`CircuitBreakerRegistry.cs`。

**Q6.7 ★★** `CircuitBreakerHealthListener` 处理哪些状态变更？为什么 Error 状态不 Trip？
代码定位：`CircuitBreakerHealthListener.cs`。

**Q6.8 ★★★** 熔断器监听器实际是怎么注册到 HealthMonitor 的？Collection 里的 `CircuitBreakerListenerRegistrar` 被注册了吗？（找代码证据）
代码定位：`DeviceServiceCollectionExtensions.cs`（Device 模块）`HealthListenerRegistrar`；Collection 的 `CircuitBreakerListenerRegistrar` 全文搜索使用点。

---

## 七、健康联动（HealthReporter ↔ DeviceHealthMonitor）

**Q7.1 ★** HealthReporter 如何把 successCount / failCount 汇总成一个信号？为什么上报异常要吞掉？
代码定位：`HealthReporter.Report`。

**Q7.2 ★★** DeviceHealthMonitor 的阈值与计数语义：默认 Failure / Recovery 各多少？连续计数如何互相清零？状态迁移发生在第几次上报？
代码定位：`DeviceHealthMonitor.ReportSuccess` / `ReportFailure`。

**Q7.3 ★★★** 完整时间线题：设备每轮采集失败（间隔 1s，熔断起步 5s），请逐步描述：第 1~2 次失败（熔断器状态？）、第 3 次失败（触发什么？）、第 4~5 轮（还采吗？）、冷却到期后（什么状态？探测怎么发生？）、探测成功（熔断器？健康状态？）、再连续成功 2 次（健康？）。指出「熔断器状态与健康状态不同步」的窗口。
代码定位：`CircuitBreaker` + `DeviceHealthMonitor` + `CollectDeviceAsync` 联动。

**Q7.4 ★★** 为什么说「熔断器恢复和健康恢复不同步」？探测成功闭合熔断器后设备仍是 Offline——此时采集还会继续吗？为什么？

**Q7.5 ★★★** HealthMonitor 通知 listener 用的是 fire-and-forget（`_ = listener.OnHealthChangedAsync(e)`）。为什么？这种写法的隐患是什么（未观察异常）？怎么缓解？
代码定位：`DeviceHealthMonitor.NotifyListeners`。

---

## 八、生命周期与优雅关闭

**Q8.1 ★★** `StopAsync` 的 drain 流程：RequestStop → 等待当前轮（多久？）→ 超时怎么办 → MarkDraining / MarkStopped 的时机。为什么在引擎里**不断开 MQTT**？
代码定位：`CollectionEngine.StopAsync` 与注释。

**Q8.2 ★★** `_roundCts` 为什么用 `CreateLinkedTokenSource`？StopAsync 取消当前轮时，轮内设备任务会怎样？

**Q8.3 ★★★** `GatewayLifecycle` 当前的读写现状：谁写入？谁读取？（全文搜索证据）这说明什么？如果由你完成闭环，ForwarderEngine 应该在哪里检查 `IsDraining` / `IsStopped`？
代码定位：`GatewayLifecycle.cs`；`rg "IsDraining|MarkDraining" src`。

**Q8.4 ★★★** `ExecuteAsync` 的 finally 里 `_roundCts` / `_currentRound` 如何清理？StopAsync 与 finally 并发读写这些字段存在竞态吗？实际会被「await 顺序」约束到什么程度？（讨论题）

---

## 九、可观测性

**Q9.1 ★★** 列出 Activity 的创建点（名称）与各自设置的 tag；失败路径 `SetStatus(Error)` 出现在哪些位置？
代码定位：`CollectionEngine` / `DeviceCollector` / `DeviceReader` / `PointValuePipeline` / `DataDispatcher` 中的 `GatewayActivities.*`。

**Q9.2 ★★** `NitroMetrics` 打了哪些指标？分别在哪些代码路径更新（Inc / Set）？
代码定位：`DeviceCollector` 中的 `NitroMetrics.CollectionTotal` / `CircuitBreakerState` / `DevicesAvailable`。

**Q9.3 ★★** 如果让你加一个「单台设备采集耗时」直方图指标，你会加在哪一层？为什么？（开放题）

---

## 十、扩展与设计权衡（开放题）

**Q10.1 ★★★** 设备从 10 台增长到 1000 台，当前设计（单轮 WhenAll + 信号量 5）的瓶颈在哪？给出 2~3 个演进方案并说明取舍。

**Q10.2 ★★★** `_lastValues` 死区缓存是内存态，重启丢失。如果要持久化，你会怎么做？代价是什么？为什么说「可能不值得」？

**Q10.3 ★★★** 数据可靠性分级：转发缓冲（两阶段出队 + 死信）是「可靠」通道，时序 Channel（DropOldest）是「尽力而为」。为什么这样设计？如果时序数据也不能丢，怎么改？

**Q10.4 ★★★** 一台设备采集卡住 30s 对整轮的影响？现有机制里哪些是保护、哪些不是？（熔断探测超时、PeriodicTimer 无堆积、WhenAll 等待）你会怎么隔离慢设备？

**Q10.5 ★★★** 手写题：不查代码，约 30 行实现一个线程安全的 Closed/Open/HalfOpen 熔断器（冷却、单探测、探测超时释放）。再对比现有实现，指出差异与取舍。

**Q10.6 ★★★** 现场讲清楚：为什么 DeviceCollector 是 Scoped 而 Reader / Pipeline / Dispatcher / 熔断器注册表是 Singleton？如果把 Pipeline 改成 Scoped 会出什么问题？（联系 `_lastValues` 与告警 Duration）
