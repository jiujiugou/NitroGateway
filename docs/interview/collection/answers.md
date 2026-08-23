# Collection 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：部分 DESIGN.md / README.md 已过时（Q5.2、Q3.1 即漂移题）。

---

## 一、架构与调度

**Q1.1 完整调用链**
`CollectionEngine.ExecuteAsync`（BackgroundService + `PeriodicTimer(_interval)`）每 tick：
1. `CreateScope()` 解析 Scoped `IDeviceCollector`，记录 `_roundCts` / `_currentRound`
2. `CollectOnceAsync`：`IDeviceManager.GetAllAsync()`（**全部设备含 Offline**）→ `IsInMaintenance` 过滤 → `DevicesAvailable` 指标 → `SemaphoreSlim(5)` 限流并发 `CollectDeviceAsync`
3. 每台设备：`CircuitBreaker.TryEnterProbe()` 熔断检查（false 跳过）→ `IDeviceReader.ReadDeviceAsync`（`IProtocolDriverPool.GetOrCreate` 复用驱动 → `driver.ReadBatchAsync(points)` → `List<RawPointValue>`）→ `IPointValuePipeline.Process`（→ `List<PointSnapshot>`，缩放+死区）→ `IDataDispatcher.DispatchAsync`（`MeasurementWriteHost.Post` 入时序 Channel + `IForwardBuffer.EnqueueAsync` 入转发缓冲 + `SinkDispatcher.Post` 事件）→ `IHealthReporter.Report` → `circuitBreaker.RecordSuccess/RecordFailure` → 指标
4. 后台三路消费者：`MeasurementWriteHost` 写 `IMeasurementStore`（SQLite）；`SinkDispatcher` 逐事件独立 scope 调所有 `IPointStoredSink`（告警/推送）；Forwarder 模块从 `IForwardBuffer` 出队 MQTT 转发
5. 健康回路：`DeviceHealthMonitor`（3 次连续失败→Offline / 3 次连续成功→Online）→ `CircuitBreakerHealthListener` Trip / Reset

**Q1.2 边界（Collection 不做的事）**
- 协议解析/驱动/长连接自愈：Protocol 模块（`IProtocolDriverPool`、`ReliableProtocolDriver`）
- 健康判定（唯一决策者）：Device 模块 `DeviceHealthMonitor`
- 存储实现：Storage 接口 + Persistence（SQLite）
- MQTT 转发（固定批量上限）：Forwarder 模块（2026-08-22 删 AIMD/死信）
- 领域模型：Domain 模块
- Collection 只做：编排调度、读→转→发→上报流水线、熔断「保护执行」、健康信号上报

**Q1.3 PeriodicTimer**
`PeriodicTimer.WaitForNextTickAsync` 每次返回后**重新开始计时**；单轮耗时超过间隔时，下一 tick 立即触发但**不会排队补偿**错过的 tick → 无轮次堆积（类注释原文）。`Task.Delay` 循环是「delay + 处理时间」，周期会漂移累积。语义差异：固定周期触发 vs 固定间隔休眠。

**Q1.4 DI scope 与生命周期**
每轮独立 scope：隔离 Scoped 依赖（`IDeviceManager` / `IPointManager` 是 Device 模块注册的 Scoped），避免跨轮共享采集器内部状态（DeviceCollector 的 `SemaphoreSlim`）。
- Scoped：`IDeviceCollector`（DeviceCollector）——轮内共享、跨轮重建
- Singleton：`IDeviceReader`（无状态）、`IPointValuePipeline`（**`_lastValues` 缓存必须跨轮共享**，Q10.6）、`IDataDispatcher`（Channel 消费者单例）、`ICircuitBreakerRegistry`（熔断状态必须跨轮）、`IHealthReporter`、`MeasurementWriteHost` / `SinkDispatcher`（HostedService 单例）、`IDeviceHealthListener`（CircuitBreakerHealthListener）

**Q1.5 为什么取全部设备（含 Offline）**
因为**熔断器自愈需要 Offline 设备参与探测**。设备 Offline → 熔断器被 Trip → 冷却到期进入 HalfOpen → 需要一轮采集来充当「探测请求」。如果 `CollectOnceAsync` 只取 Online 设备：设备变 Offline 后熔断器 Open→HalfOpen 后永远没有采集任务去碰它，而 HealthMonitor 的 Online 信号只能来自采集成功 → **死锁，设备永远无法自愈**。所以取全部设备，「实际采不采」交给熔断器（`TryEnterProbe()`）决定；维护模式是唯一在流水线外过滤的状态（业务语义：暂停采集与告警）。

**Q1.6 单轮异常**
不会杀死引擎。catch Exception → LogError → `Task.Delay(_errorRetryDelay, 默认 5s)` → 下一轮（PeriodicTimer 从 delay 后重新计时）；`OperationCanceledException` → break（正常关闭，不是故障）。引擎的 catch 是兜底（如 scope 创建失败）；`CollectOnceAsync` 内部自己已 try/catch（单轮内异常记录后结束）。测试：`CollectionEngineTests.RoundFailure_DoesNotStopEngine_RetriesNextRound`。

---

## 二、单设备采集流水线

**Q2.1 五步（注意顺序）**
1. 熔断检查：`TryEnterProbe()` 返回 false → 跳过（Debug 日志，直接 return）
2. 读取：失败 → `Report(0, 1, err)` + `RecordFailure` + 指标(failure) + Activity Error → return；成功继续
3. 转换：`Pipeline.Process` → snapshots
4. 分发：`snapshots.Count > 0` → `DispatchAsync`；否则警告「没有有效点位数据，跳过分发」
5. 健康上报：goodCount / failCount → `Report`（**在熔断恢复之前**）→ `RecordSuccess` + 指标(success) + Activity Ok

**Q2.2 读取失败 vs 点位质量差（核心区别）**
- 读取失败：熔断器 `RecordFailure`（仅 HalfOpen 生效，Closed 忽略）+ 健康 `ReportFailure`（failCount=1）
- 点位质量 Uncertain：健康 `ReportFailure`（failCount = snapshots.Count - goodCount > 0 → HealthMonitor.ReportFailure）——**但熔断器仍 `RecordSuccess`**
两个决策者视角不同：熔断器判断「链路通不通」（读成功=通，推进探测闭合）；健康判断「数据质量好不好」（Uncertain 也计入失败）。注释原文：「读取成功（含点位质量差）只上报成功以推进探测判定」。

**Q2.3 并发限流**
`SemaphoreSlim(maxConcurrency 默认 5)`，`WaitAsync(ct)` 在 try 外（取消时无持有），finally `Release()`。DeviceCollector 是 Scoped → **每轮新实例 → 限流是「轮内」上限，不是全局**。改成 Singleton 后果：信号量变全局（跨轮累积等待），且所有实例字段跨轮共享——与「避免跨轮共享内部状态」的设计意图冲突。

**Q2.4 维护模式过滤**
`IsInMaintenance`: `(_healthMonitor.GetSnapshot(device.Id)?.Status ?? device.Status) == DeviceStatus.Maintenance`。以 HealthMonitor 实时快照为准（ADR-002 P2-2）：设备目录缓存里的 Status 可能滞后一个采集周期；实时快照零延迟。fallback：设备未注册 monitor（如历史数据）时回退到配置中的 `device.Status`。

**Q2.5 不抛异常契约**
正常路径全部走 OperationResult / 降级日志（读取失败 return、分发失败记录日志、缩放失败 Uncertain），所以不抛。若意外抛异常：`CollectOnceAsync` 的 `Task.WhenAll` 抛出 → 外层 catch 记日志 → 整轮结束；**其他设备 task 不受影响**（WhenAll 等所有任务完成，异常聚合返回）。但该设备后续步骤（健康上报、熔断恢复、指标）会跳过——所以契约很重要。

---

## 三、数据读取

**Q3.1 连接策略演进**
DESIGN.md 说「v1 每轮连接/断开」，**实际实现是 `IProtocolDriverPool.GetOrCreate(device)` 复用长连接**，断线恢复由 `ReliableProtocolDriver` 的建连/重试管线负责（类注释）。演进原因：每轮 TCP 握手/关闭开销大；对 PLC/现场网络频繁连接不友好；复用 + 断线自愈降低延迟与负载。DESIGN.md 未更新，属文档漂移。

**Q3.2 无启用点位返回空列表=成功**
设备可能只是暂时没配置点位，不是通信故障；空列表让 Pipeline 产出空快照 → 跳过分发 → 健康上报 (0,0) → ReportSuccess。错误语义只留给真正的通信失败。

**Q3.3 异常转换**
`OperationCanceledException` 重抛；其他 Exception → `OperationalError.Protocol("设备读取异常：...")`。取消是**控制流不是故障**：上层（引擎循环退出、StopAsync 取消当前轮）依赖 OCE 协调；转成 OperationResult 失败会吞掉取消语义，取消后任务还会继续跑。

**Q3.4 批量 vs 逐点**
`ReadBatchAsync(points, ct)` 一次把整台设备点位交给驱动，由 Protocol 层负责合并连续地址/寄存器分组，减少协议往返。DeviceReader 不感知分组细节（v1）；模块 DESIGN.md 提到 v2 计划 `PointBatchOptimizer` 做跨协议批量优化分组。

---

## 四、值转换管道

**Q4.1 输入输出与可变状态**
输入 `RawPointValue` 列表（协议解码后、未缩放）→ 输出 `PointSnapshot` 列表（含 DeviceId/PointName/DataType 自描述冗余）。职责：类型透传 / 工程缩放（×ScaleFactor + ScaleOffset）/ 死区缓存更新。唯一可变状态：`ConcurrentDictionary<Guid, double> _lastValues`（内存态，重启丢失）。Process 本身无 IO、无锁获取（字典自带并发安全）。

**Q4.2 死区语义（最容易答错）**
死区**不丢弃数据**！快照照常下发，SignalR 推送、存储写入、转发全部不受影响。死区只影响「上次工程值缓存」的更新（供告警 Duration 判定）——值在死区内时不刷新缓存基准，避免微小抖动把 Duration 告警反复重置。测试 `Deadband_SmallChange_PassesThrough_ButDoesNotRefreshCache` 明确断言：数据照常输出 60.25，缓存仍保持 60.0。设计意图：数据可靠性优先——若死区丢数据，时序库会缺值、云端看不到微变。

**Q4.3 缩放失败**
catch → 返回 Uncertain 快照（`ErrorMessage = "缩放失败：无法转换为数值"`），**不抛、不丢弃、不影响其他点位**（逐点位独立转换）。保留原因：可追溯（现场调试）、下游可见质量问题、健康统计计入 failCount。下游：存储照写（Quality=Uncertain）、告警可识别质量、转发透传质量码。

**Q4.4 Bool/String 与白名单**
Bool/String：直接透传，`Quality = Good`，不做缩放与死区。数值白名单 8 种：Float / Double / Int16 / UInt16 / Int32 / UInt32 / Int64 / UInt64。其余（含未知枚举值）走透传分支（宽松处理，不抛异常）。

**Q4.5 GetLastValue / SetLastValue**
告警模块的 Duration 判定读取（Alarm 评估时长类告警需要「上次工程值」基准）。缓存内存态 → 重启丢失 → 死区基准重置，**首个值总是通过**（测试 `Deadband_NewPipeline_FirstValueAlwaysPasses`），告警 Duration 以重启后首个值为基准——存在一次性的误判窗口，可接受。

**Q4.6 死区判定细节**
条件：`point.Deadband > 0 && TryGetValue(last) && |eng - last| < Deadband` → 不更新缓存；否则 `_lastValues[point.Id] = engValue`。首次值（无 last）→ 总是更新；`Deadband = 0` → 跳过死区逻辑，每次更新；比较是**绝对值**（非百分比）。「值在死区内」时缓存保持**原基准值**（如 60 → 60.25 → 60.1，基准一直是 60）。

---

## 五、数据分发与异步写入

**Q5.1 双写与三步骤**
① `MeasurementWriteHost.Post`（有界 Channel → 后台批量写 `IMeasurementStore`）② `IForwardBuffer.EnqueueAsync`（转发缓冲，MQTT 数据源）③ `SinkDispatcher.Post`（事件 Channel → 各 `IPointStoredSink`）。互不阻塞：Post 是 `TryWrite` 非阻塞；EnqueueAsync 失败只记日志（按 Severity 分级）；事件推送非阻塞。设计意图（类注释）：采集热路径只做入队，落库与事件消费异步化，避免慢速订阅方阻塞采集循环。

**Q5.2 DispatchAsync 返回什么（文档漂移）**
实际**总是返回 Success**（空快照提前返回；三步骤独立失败降级为日志告警）。DESIGN.md「任一失败返回 Error」是**过时描述**；接口 XML 注释为准：「任一步骤失败不阻断其余步骤」。面试加分：指出代码与 DESIGN.md 不一致，并以接口注释/代码行为为准。

**Q5.3 MeasurementWriteHost**
容量 1000 批，`BoundedChannelFullMode.DropOldest`。选 DropOldest：数据按时间戳旧→新，**最旧数据价值最低**（丢最旧优先）；DropWrite 会丢最新数据（更差）；Wait 会阻塞采集热路径（绝对不行）。`Post` 返回 false → DataDispatcher 记 Warning「Measurement Channel 已满，丢弃数据」。单批写入异常：catch 记日志，跳过该批继续消费——落库故障不阻塞采集。

**Q5.4 数据会丢吗 / 可靠性分级**
时序库层面会丢（DropOldest），但**转发缓冲是独立同步写入**（`EnqueueAsync` 成功才继续），MQTT 转发数据不丢。可靠性分级：Buffer（SqliteForwardOutbox 两阶段出队 InFlight + 重试/超限丢弃）是「可靠」主通道；时序 Channel 是「尽力而为」（本地历史/可视化用途）。若时序也不能丢：加背压（落库失败暂停采集并告警）、增大容量 + 批量合并、或时序也走持久化队列（成本高）。

**Q5.5 SinkDispatcher**
每个事件独立 scope：Sink 可注入 Scoped 依赖（如 DbContext / DeviceManager）。单 Sink 异常 catch 记日志，不影响其他 Sink 与后续事件；消费循环外层还有兜底 catch。`Dispose`：`Writer.TryComplete()` → 不再接受新事件，后台消费完剩余事件后退出（优雅排空）。Channel 1000 条 DropOldest，满时 Warning。

**Q5.6 DataType 透传**
ADR-001 P1-5：此前转发 payload 恒按 Float 序列化，云端把 Bool/Int/String 都当 Float 解析（错）。`PointSnapshot` 增加 `DataType` 冗余字段（构造时由 `DevicePoint.DataType` 填充），`ToBatchMeasurements` 透传 `s.DataType`。测试：`DataType_PropagatedToSnapshot`（Pipeline）+ `DataDispatcherTests`（类型透传）。

---

## 六、熔断器

**Q6.1 状态机**
- Closed：正常放行
- `Trip()`（HealthMonitor Offline 信号）→ Open，冷却复位 5s
- Open：`TryEnterProbe()` 返回 false 拒绝；冷却到期（`ComputeState` 惰性检查，由 `TryEnterProbe()` 触发）→ HalfOpen
- HalfOpen：首次 `TryEnterProbe()` 认领探测放行（`_probing = true`）；已有探测在途 → 拒绝
- 探测成功 `RecordSuccess()` → Closed（冷却复位）
- 探测失败 `RecordFailure()` → Open（冷却 ×2，封顶 5min）
- `Reset()`（HealthMonitor Online 信号 / 手动干预）→ Closed（冷却复位）

**Q6.2 职责拆分**
HealthMonitor 是「唯一健康决策者」（SST，单写者）：业务阈值（连续失败 3 / 成功 3）、状态迁移、通知监听器。CircuitBreaker 只做「执行保护」：按 Open/HalfOpen 决定放行/拒绝、管理冷却与探测。好处：健康判定是业务语义（阈值可配），保护执行是运行时机制（时间驱动）；互不感知细节，可独立演进、独立单测（`CircuitBreakerTests` 只管状态机，`DeviceHealthMonitorTests` 只管阈值）。

**Q6.3 冷却退避**
Trip → 5s；探测失败 → ×2（5→10→20→40→…）封顶 5min；`RecordSuccess`（HalfOpen→Closed）或 `Reset` → 复位 5s。封顶原因：避免无限退避导致恢复时间过长（设备已恢复也要等很久）；5min 是业务可接受的最大探测间隔。测试 `HalfOpen_ProbeFailure_DoublesCooldownEachTime`。

**Q6.4 单探测与 CQS（命令/查询分离）**
`_probing`（锁内）：HalfOpen 且无探测 → `TryEnterProbe()` 认领（`_probing=true, _probeStarted=now`）返回 true 放行；已有探测 → 返回 false 拒绝。`_probeStarted` 用于 30s 探测超时自动释放 `_probing`（防止探测请求丢失/卡死 → 永远无探测）。`TryEnterProbe` 是**带副作用的命令**（惰性状态迁移 + 认领探测），`State` 是**纯查询**（CQS）：采集执行路径调 `TryEnterProbe`——原子合并迁移+认领，避免「检查与认领之间」的竞态窗口；诊断/只读路径只读 `State`。历史实现 `IsOpen` 属性带副作用，导致 StatusController 轮询会抢占探测名额、饿死自愈探测（ADR-015），故拆分为命令/查询。测试 `HalfOpen_OnlyOneProbeAllowed` / `State_Getter_IsPureQuery_DoesNotTransitionOrConsumeProbe`。

**Q6.5 RecordFailure 在 Closed 被忽略**
熔断器**不自己判定故障**：Trip 只能由 HealthMonitor Offline 信号触发，Closed 下连续失败不会打开熔断器（测试 `RecordFailure_InClosed_DoesNotOpen`——10 次也纹丝不动）。真正打开的两条路径：① Offline 信号 `Trip()`（第 3 次连续失败）；② HalfOpen 探测失败 `RecordFailure()`（冷却翻倍回 Open）。所以熔断器是「第二道防线 + 探测机制」，第一道是健康阈值。

**Q6.6 Registry**
`ConcurrentDictionary<string, ICircuitBreaker>` + `GetOrAdd` 惰性创建（设备首次被采集才建，此后复用）；字典自身线程安全。`Reset(deviceId)` 不存在时 no-op。`GetAll()` 返回只读快照（诊断/管理面板）。设备删除后实例**常驻内存**（随网关生命周期）——设备数量级小可接受；若要回收需监听设备删除事件（当前没有）。

**Q6.7 Listener 处理的状态**
Online → `Reset()`；Offline → `Trip()`；Unknown / Error / Maintenance **不处理**。Error 不 Trip 的原因：Error 仍允许采集探测（Trip 会停止所有探测导致自愈无门）；Maintenance 由 `IsInMaintenance` 过滤（根本不采集），无需熔断。

**Q6.8 实际注册路径（找代码证据）**
真正注册：Device 模块的 `HealthListenerRegistrar`（HostedService）构造时解析 `IEnumerable<IDeviceHealthListener>`（包含 Collection 注册的 `CircuitBreakerHealthListener`）→ `monitor.AddListener`。**Collection 自己的 `CircuitBreakerListenerRegistrar` 定义了但从未被注册**（`AddNitroCollection` 里没有 `AddHostedService`，全文搜索无使用点）——冗余/遗留代码，其「构造即注册」注释有误导性。面试可指出：要么注册它，要么删除，二选一。

---

## 七、健康联动

**Q7.1 HealthReporter**
`failCount > 0` → `ReportFailure(deviceId, errorMessage ?? "采集失败")`；否则 `ReportSuccess`。异常吞掉（catch {}）：健康上报是旁路，失败不能崩采集主循环。

**Q7.2 阈值与计数**
默认 `FailureThreshold = 3`、`RecoveryThreshold = 3`（DI 可配）。连续语义：ReportSuccess 清空失败计数、成功计数 +1；ReportFailure 清空成功计数、失败计数 +1。迁移：失败计数 == 3 → 发 Offline 信号（当前非 Offline 时）；成功计数 == 3 → 发 Online 信号（当前非 Online 时）。触发迁移后对应计数清零。

**Q7.3 完整时间线（间隔 1s，熔断起步 5s）**
1. 第 1~2 轮失败：熔断器 **Closed**（RecordFailure 被忽略）；健康失败计数 1 → 2
2. 第 3 轮失败：熔断器仍 Closed（照常采集）；健康计数 3 → **Offline 信号 → Trip → 熔断器 Open**（冷却至 t+5s）
3. 第 4~5 轮：`TryEnterProbe() = false` → 跳过（冷却未到期）
4. t+5s 后某一轮：`TryEnterProbe()` 发现冷却到期 → **HalfOpen** → 放行探测（真实读取）
5. 探测失败 → Open（冷却 10s）；探测成功 → `RecordSuccess` → **Closed**（但健康仍是 Offline，失败计数从 1 重新累计）
6. 熔断器 Closed 期间每轮照常采集：连续 3 次成功 → **Online 信号 → Reset**（此时已 Closed，no-op）
不同步窗口：探测成功闭合熔断器 ≠ 设备 Online；「设备 Offline + 熔断器 Closed」期间仍每轮采集（CollectOnce 取全部 + TryEnterProbe()=true），成功会累积健康恢复计数。

**Q7.4 不同步的含义**
熔断器恢复（一次探测成功）比健康恢复（3 次连续成功）快，由不同信号驱动（RecordSuccess vs ReportSuccess 累积）。中间态采集继续，且中途再失败也不会立即 Open（Closed 下 RecordFailure 忽略），要重新累积 3 次失败 → Offline → Trip。

**Q7.5 fire-and-forget 的隐患**
原因：监听器是旁路（熔断、持久化），不能因监听器慢/异常阻塞健康状态迁移主路径。隐患：`_ = listener.OnHealthChangedAsync(e)` 若 listener 抛异常，Task faulted 且**未观察** → 触发 `UnobservedTaskException`（默认不崩进程但全局事件记录，且异常被静默吞掉难以排查）。缓解：listener 内部自兜底（try/catch 包住逻辑）、或 `NotifyListeners` 里 await + 逐 listener try/catch、或注册 `TaskScheduler.UnobservedTaskException` 兜底。这是可讨论的深水改进点。

---

## 八、生命周期与优雅关闭

**Q8.1 StopAsync 流程**
1. Log → `_lifecycle.RequestStop()`
2. 取 `_currentRound`；非空 → `Task.WhenAny(current, Task.Delay(30s))` 等当前轮完成
3. 超时 → Warning → `_roundCts?.Cancel()` → 再 `WhenAny(current, Task.Delay(5s))` → `_lifecycle.MarkDraining()`
4. `_lifecycle.MarkStopped()` → `base.StopAsync`
**不在引擎断 MQTT**：注释原文——由 Host 层 GracefulShutdown 统一管理，过早断开会让 `ForwarderEngine` 来不及排空转发缓冲（MQTT 断连 → 缓冲堆积 → 数据滞留本地）。

**Q8.2 linked token**
`_roundCts = CreateLinkedTokenSource(stoppingToken)`：把宿主停止令牌链到轮内令牌。引擎主循环退出后轮内任务可能仍在跑，StopAsync 30s 超时后 `_roundCts.Cancel()` 能传播到所有轮内任务（读操作、WaitAsync、WhenAll）；同时轮内任务也响应宿主令牌（正常关闭路径）。每轮 finally `Dispose()`。

**Q8.3 GatewayLifecycle 现状**
全文搜索证据：只有 CollectionEngine 写入（RequestStop / MarkDraining / MarkStopped），**没有任何读取方**（`IsDraining` / `IsStopped` 无人读，Forwarder 未接入）。说明「采集→转发 drain 协调」是渐进式实现：标志位已铺好、消费方未接。若完成闭环：ForwarderEngine 每轮开始前查 `IsDraining`（true 则停止出队新批次、排空缓冲后退出）；`IsStopped` 作为最终停止信号。

**Q8.4 竞态讨论**
finally 里 `_roundCts?.Dispose(); _roundCts = null; _currentRound = null`。StopAsync 与 finally 无锁并发访问这两个字段。实际被 await 顺序约束：`_roundCts?.Cancel()` 只在 current **未完成**时执行（WhenAny 超时分支），此时 finally 尚未运行（finally 在 await current 返回后执行）→ 正常路径不会 Cancel 已 Dispose 的 CTS。理论窗口：轮内 task 恰好在这几行之间完成，finally Dispose 与 StopAsync Cancel 并发（`CancellationTokenSource.Cancel()` 对 disposed 实例抛 ObjectDisposedException）。极小窗口、可接受；改进：加锁或 StopAsync 只依赖 stoppingToken。面试能指出窗口并给出取舍即算深水过关。

---

## 九、可观测性

**Q9.1 Activity**
- `CollectRound`（整轮，tag `DeviceCount`）
- `CollectDevice`（单台，tag `DeviceId` / `DeviceName`）
- `ReadDevice`（tag `DeviceId` / `DeviceProtocol`）
- `Pipeline`（tag `DeviceId` / `SnapshotCount`）
- `Dispatch`（tag `DeviceId` / `SnapshotCount`）
失败路径 `SetStatus(Error)`：读取失败（含 `ErrorMessage` tag）、CollectOnce 取消/异常。成功路径 `SetStatus(Ok)`。

**Q9.2 Metrics**
- `NitroMetrics.CollectionTotal{device_id, result=success|failure}`：读取成功/失败路径 `.Inc()`
- `NitroMetrics.CircuitBreakerState{device_id}`：读失败/读成功路径 `.Set((int)State)`
- `NitroMetrics.DevicesAvailable`：CollectOnce 过滤维护后 `.Set(devices.Count)`

**Q9.3 采集耗时直方图（开放）**
建议加在 `CollectDeviceAsync` 外层（含熔断+读+转+发+上报全流程）与 `ReadDeviceAsync`（协议层耗时）两层：前者回答「单台设备采集周期多长」，后者回答「是设备慢还是落库慢」。Prometheus Histogram，bucket 参考采集间隔（50ms / 100ms / 250ms / 500ms / 1s / 5s）。

---

## 十、扩展与设计权衡

**Q10.1 1000 台设备瓶颈与演进**
瓶颈：① 单轮 `WhenAll` 串行等待所有设备——慢设备拖全轮；② 信号量 5 限制吞吐；③ 固定 1s 轮询无错峰。演进：① 按设备独立调度（每设备周期任务/时间轮），互不阻塞；② 按协议/网络分区并行，每片独立并发上限；③ 协议层批量优化（PointBatchOptimizer 合并连续寄存器）；④ 快慢设备隔离 + 每设备超时；⑤ 故障设备动态退避（熔断已做一半）。取舍：复杂度 vs 规模，前两个对吞吐最有效。

**Q10.2 死区缓存持久化**
方案：① 启动时从时序库读最近一条工程值初始化 `_lastValues`（简单，但依赖时序库保留策略）；② 独立状态表异步写、重启读（写放大）；③ WAL/检查点。代价：写入放大、启动延迟、且**破坏 Pipeline「无 IO 纯计算」的边界**（需抽 `ILastValueStore` 接口）。为什么可能不值得：死区只是告警 Duration 的辅助，重启后首个值即基准，影响仅一次误判窗口。

**Q10.3 可靠性分级**
Buffer（SqliteForwardOutbox 两阶段出队 Pending→InFlight→Commit/丢弃 + 重试）→ 云端数据**可靠**；时序 Channel DropOldest → 本地历史**尽力而为**。原因：云上报是核心业务（监控中心），本地历史是辅助（可视化/审计可接受少量缺失）；Buffer 入队是同步成功（await EnqueueAsync），时序走内存 Channel 异步。若时序不能丢：背压（落库失败暂停采集+告警）、容量调大+批量合并、或时序也走持久化队列（成本高、与 SQLite 写放大斗争）。

**Q10.4 慢设备影响与隔离**
`WhenAll` 等所有设备 → 整轮被慢设备拖长；PeriodicTimer 不堆积（下一轮立即开始）但每轮都被拖；设备卡 30s → 每轮 30s，吞吐崩塌。现有保护：熔断探测 30s 超时释放（只是 HalfOpen 探测锁，**不是读超时**）；协议层驱动超时；信号量只限并发不限时长。隔离方案：每设备 `CancellationTokenSource.CancelAfter(超时)` + 超时计为失败（推进健康/熔断）；慢设备独立调度队列。

**Q10.5 手写熔断器对比**
要点：锁 + 状态字段 + 冷却检查（惰性转 HalfOpen）+ 单探测认领 + 探测超时释放 + 冷却翻倍封顶。对比现有实现：`TryEnterProbe()` 命令合并了迁移+认领（原子、调用约定简单），`State` 是纯查询（CQS，读路径安全）；探测超时固定 30s；Trip/Reset 由外部信号驱动（不自判）。面试亮点：指出「状态迁移放在 TryEnterProbe 检查点」消除检查与认领之间的竞态窗口，命令/查询分离避免诊断路径误触探测（ADR-015）。

**Q10.6 Scoped vs Singleton 的关键**
DeviceCollector Scoped：信号量轮内限流、不跨轮共享状态、适配 Scoped 的 `IDeviceManager`。Reader/Pipeline/Dispatcher/熔断器 Singleton：**Pipeline 的 `_lastValues` 必须跨轮共享**——死区基准与告警 Duration 都依赖跨轮记忆；熔断器状态必须跨轮；Channel 消费者必须单例。若 Pipeline 改 Scoped：每轮新实例 → `_lastValues` 全空 → 死区永远按「首个值」判定 → 缓存基准失效、`GetLastValue` 拿不到上次值（Alarm Duration 判定错乱）。这是破坏性变更。
