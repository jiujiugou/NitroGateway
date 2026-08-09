# Forwarder 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：DESIGN.md 存在漂移点（Q1.6）。

---

## 一、架构与数据流

**Q1.1 完整调用链**
`ForwarderEngine` 每轮（首轮立即 + 之后每 5s）：
1. `GetCountAsync` 查积压 → 独立 scope 解析 `IMqttClient` / `IForwarder`
2. `State == Connected` 才调 `ForwardBatchAsync(MaxDrainPerRound=2000)`
3. `takeCount = Math.Min(maxCount, _throttle.MaxBatchSize)`（`Forwarder.cs:69`）
4. `IForwardBuffer.DequeueAsync`：同一事务 SELECT Pending 行 + UPDATE 为 InFlight，事务外反序列化 → `List<BatchMeasurements>`
5. 逐批：`ApplyDelayAsync`（节流延迟）→ `IMessageSerializer.Serialize(batch)` → JSON UTF-8 字节 → `IMqttClient.PublishAsync("nitrogateway/{deviceId}/measurements", payload, qos: 1)`（返回 `OperationResult`，不抛异常）
6. 成功 → 收集 id 待 Commit；失败 → `MarkFailedAsync`（retry+1，≥5 进 DeadLetter）
7. 轮末 `CommitAsync(committed)` 物理删除成功批次；更新指标与 Activity
数据形态：DB 行（payload JSON 字符串）→ `BatchMeasurements` 对象 → `byte[]` JSON → Broker 消息体。
测试：`ForwarderIntegrationTests.Forwarder_PublishesBatch_AndCommitsBuffer`。

**Q1.2 边界（Forwarder 不做的事）**
- 指令下行（云端→边缘）：README.md 明确「尚未实现」
- 消费确认（云端真的处理了）：v1 不做，Publish 成功即 Commit（v4 应用层 ACK 才做）
- Broker 连接/重连：Transport.MQTT 的 `MqttHostedService`；Forwarder 只读 `State`
- 存储实现：Persistence（SQLite）；Forwarder 只依赖 `IForwardBuffer` 接口
- 序列化格式决策：由 `IMessageSerializer` 实现决定（JSON/Protobuf/压缩）
- Forwarder 只做：定时取数、序列化、发布、结果落库（Commit/MarkFailed）、节流反馈、可观测性

**Q1.3 返回语义**
- 仅 Dequeue 失败返回 `Failure`（+ Error 日志 + Activity Error）；其余一律 `Success`
- 单批失败已 MarkFailed → 由缓冲的重试/死信机制表达，调用方无需感知个别批次结果（`IForwarder.cs` 语义约定）
- 空队列 Success；Commit 失败也返回 Success（但 Error 日志 + Activity Error）
- 原因：单批失败不阻塞整体；若整体返回 Failure，调用方可能整轮重试，反而把已成功批次重复发布

**Q1.4 IMessageSerializer 抽象**
- 解耦格式：Forwarder 只依赖接口产字节，v2 换 Protobuf/压缩不用动核心循环，只换 DI 注册
- `ContentType`（"application/json"）：随消息语义告知消费端负载格式，供云端识别
- 测试：`ForwarderIntegrationTests` 中 `JsonMessageSerializer` 直接注入使用

**Q1.5 DI 生命周期**
- `ForwardingThrottle` Singleton：AIMD 状态必须跨轮持久；scoped 会在每轮重置为初始值 → 节流失效（DI 注释原文）
- `IMessageSerializer` / `IForwarder` Singleton：无状态（依赖均为 Singleton/无状态）
- `IMqttClient` 也是 Singleton（`MqttServiceCollectionExtensions.cs`，MqttClientWrapper + MqttHostedService）
- 引擎每轮独立 scope：工程约定，隔离作用域；即使当前解析对象都是 Singleton，也避免未来 IForwarder 引入 scoped 依赖时出现生命周期泄漏

**Q1.6 单轮出队量**
- `Math.Min(maxCount, _throttle.MaxBatchSize)`；引擎传入 `MaxDrainPerRound = 2000`（`ForwarderEngine.cs:28,127`），throttle 初始 1000 → 实际首轮 ≤ 1000，持续失败可降到 100
- 漂移点：DESIGN.md v1 决策表「Dequeue 全量（maxCount=int.MaxValue）」是设计时描述；实现已加引擎上限 + throttle 双重限制，**以代码为准**

---

## 二、缓冲与两阶段状态机

**Q2.1 状态机**
- Pending：待转发，可被 Dequeue（`Count` / `GetCountAsync` 只统计 Pending，不含死信）
- InFlight：已出队未确认（SELECT+UPDATE 同一事务标记）；不计 Count、不再被 Dequeue
- 迁移：成功 → `CommitAsync` 物理删除；失败 → `MarkFailedAsync` retry_count+1，未超限回 Pending，超限（默认 5）→ DeadLetter
- 启动恢复：遗留 InFlight → Pending（P0-1①）
- DeadLetter：`GetDeadLettersAsync` 查询；`RetryDeadLetterAsync` 重置计数回 Pending；`DiscardDeadLetterAsync` 物理删除
- Dequeue「只返回不移除」：两阶段提交，进程崩溃时批次不丢（可能重复，但不丢），重启后恢复重发

**Q2.2 DequeueAsync 事务**
- SELECT + UPDATE 同一事务：保证「取出的批次必被标记 InFlight」原子性，防止取重/漏标
- 反序列化放事务外：负载可能损坏，损坏行要单独走 `MarkFailedAsync`（需开启新事务）；事务内处理会拖长锁时间；事务已提交，损坏行不会卡 InFlight
- 空队：直接 commit 并返回空列表

**Q2.3 损坏行**
- `RecoverCorruptRowAsync` → 复用 `MarkFailedAsync`：retry_count+1，超限进 DeadLetter，否则回 Pending（下轮再试，最多 5 次）
- 不能留 InFlight：InFlight 不计 Count、不再出队、只靠进程重启恢复 → 一条坏数据会导致静默丢数（P0-1②）
- 恢复本身失败仅 LogError，不影响其余行出队

**Q2.4 启动恢复**
- 构造函数执行 `UPDATE forward_buffer SET status='Pending' WHERE status='InFlight'`，恢复 N>0 记 Warning（「上次进程可能异常退出」）
- 恢复失败仅告警不阻断启动（下次启动仍会重试）
- 语义：崩溃窗口内「已发布未 Commit」的批次会重发 → 重复投递由云端幂等兜底

**Q2.5 MarkFailedAsync**
- 一次 UPDATE 完成：`status = CASE WHEN retry_count+1 >= @max THEN 'DeadLetter' ELSE 'Pending' END`、`retry_count+1`、`last_error=reason`（P2-11，3 次往返 → 2 次）
- 事务外再 SELECT 一次判断是否进死信，仅供 Warning 日志
- 默认 `maxRetries = 5`
- 测试：`SqliteForwardBufferTests.MarkFailed_OverMaxRetries_MovesToDeadLetter`

**Q2.6 死信操作约束**
- Retry：仅 `WHERE status='DeadLetter'` 才重置为 Pending（retry_count=0、last_error=NULL）
- Discard：仅 DeadLetter 才物理删除
- 不存在或不在死信状态 → `OperationalError.NotFound`：防止误操作 InFlight/Pending 批次

---

## 三、失败路径与可靠性

**Q3.1 Dequeue 失败**
- 返回 Failure + `LogError`（P1-3①）：否则出队异常被吞，批次卡 Pending、转发静默停滞且无信号
- `activity.SetStatus(Error, message)`；缓冲原状保留，下轮重试
- 测试：`ForwarderFailureTests.ForwardBatchAsync_DequeueFailure_ReturnsFailureResult` / `_LogsError`；`ForwarderActivityTests.ForwardBatchAsync_DequeueFailure_SetsActivityError`

**Q3.2 Publish 失败**
依次：`LogWarning` → `OnMqttFailure()`（节流收紧）→ `MarkFailedOrLogErrorAsync`（重试/死信）→ `ForwardTotal.WithLabels("failure").Inc()` → `anyFailure=true` → Activity Error
- 方法仍返回 Success；后续批次继续处理（坏消息隔离，不阻塞）
- 失败批次不进 committed 列表，不会被误删
- 测试：`ForwarderFailureTests.ForwardBatchAsync_PublishFailure_MarksFailedForRetry`

**Q3.3 异常路径差异**
- 返回失败：`LogWarning`（Broker 拒绝类）
- 抛异常：`LogError` + `activity.SetTag(ErrorMessage, ex.ToString())`（本地/网络异常类）
- 相同：OnMqttFailure + MarkFailed + failure 指标 + Activity Error
- 即「远端拒绝」与「本地异常」用日志级别区分，行为一致

**Q3.4 Commit 失败**
- 已发布批次卡 InFlight：不参与 Count、不再出队、仅进程重启时恢复 → 数据实际已转发但无法确认，属静默丢数风险（P1-3②）
- `LogError` + Activity Error；方法仍返回 Success（发布本身成功）
- 重启后这些批次回 Pending 重发 → 重复
- 测试：`ForwarderFailureTests.ForwardBatchAsync_CommitFailure_LogsError`

**Q3.5 MarkFailed 失败**
- 后果同 Commit 失败：批次卡 InFlight；`LogError`「批次将卡 InFlight」（`Forwarder.cs:157-169`）
- 恢复时机：仅进程重启（构造函数恢复逻辑）
- `MarkFailedOrLogErrorAsync` 是显式封装：先检查结果再记日志，避免失败被吞
- 测试：`ForwarderFailureTests.ForwardBatchAsync_MarkFailedFailure_LogsError`

**Q3.6 at-least-once 分析**
- 不丢：数据先落 SQLite（Pending）再发布，发布成功才删除
- 重复窗口：① Publish 成功 → Commit 前崩溃 → 重启重发；② QoS1 本身允许 Broker 收到但 ACK 丢失 → 客户端重发；③ 死信 Retry 重置计数后重发
- 云端兜底：按 BatchId / Record.Id 幂等去重（v1 语义为至少一次，非恰好一次）

---

## 四、AIMD 节流

**Q4.1 参数**
- `MaxBatchSize ∈ [100, 1000]`，初始 1000；失败 `/2`（下限 100），成功 `+10`（上限 1000）
- `DelayMs ∈ [0, 200]`，初始 0（不延迟）；失败 `+20ms`（上限 200），成功 `-5ms`（下限 0）
- `ApplyDelayAsync` 仅当 DelayMs>0 时 `Task.Delay`
- 测试：`NewThrottle_DefaultState` / `ThreeFailures_ShrinksBatchAndIncreasesDelay` / `RepeatedFailures_HitsFloor` / `Success_DoesNotExceedMax`

**Q4.2 收紧快、恢复慢**
- 失败减半：1000→500→250→125→100，数轮内把压力降到安全区间
- 成功 +10：缓慢恢复避免吞吐抖动（与 TCP 拥塞控制 AIMD 同源，类注释原文）
- 目的：MQTT 恢复瞬间不冲垮 Broker

**Q4.3 Singleton 原因**
- 状态必须跨调度轮持久；scoped 每轮新建 → 每轮都从 1000/0 开始，节流记忆丢失，恢复瞬间依旧冲垮 Broker（DI 注释原文：「若按作用域注册会在每轮重置为初始值，节流失效」）

**Q4.4 线程安全假设**
- 无锁，依赖 Forwarder 单线程顺序调用（每轮一个转发循环逐个反馈成功/失败，类注释原文）
- 失效场景：若并行发布（多批次并发、各自 await 后调用 OnMqttSuccess/Failure），读改写出现竞态（基于过期值计算）
- 修复方向：Interlocked / 锁，或按轮汇总后统一反馈

**Q4.5 全局共享副作用**
- 一台设备持续坏消息会让全局节流收紧（批量减半 + 延迟上升），拖慢所有设备
- v1 单 Broker 场景可接受（ADR-001 P3-14 明确不修）；多 Broker/多租户时应按设备/租户隔离 throttle 实例

**Q4.6 取消语义**
- `Task.Delay(DelayMs, ct)` 取消 → `OperationCanceledException`
- 该调用在 `Forwarder` 的 try 之外（`Forwarder.cs:97`）→ 冒泡到引擎 `catch (ex is not OperationCanceledException)` 不匹配 → 由 `ExecuteAsync` 的 `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` 按正常停机处理（不当作故障）

---

## 五、引擎与调度

**Q5.1 首轮立即执行**
- do-while：先 `RunRoundAsync` 再 `WaitForNextTickAsync`；避免启动后空等一个周期（P3-12）
- 测试：`ForwarderEngineTests.FirstRound_RunsImmediately_WithoutWaitingFullInterval`

**Q5.2 未连接跳过**
- 避免空转出队：Dequeue 会把批次翻成 InFlight，未连接时发布必失败 → 无谓消耗重试计数并产生 InFlight 往返
- 只放行 `Connected`；Disconnected / Connecting / Reconnecting / Faulted 均跳过（`MqttConnectionState.cs`）
- Faulted 表示超过最大重试次数、需外部介入（Transport 层语义），引擎侧同样跳过

**Q5.3 积压告警**
- 阈值 1000 批；首次超限立即告警；之后每 60s 一次；积压回落后重置为 `MinValue` → 下次超限立即再告警（P2-8）
- 目的：断线期间每 5s 一轮会刷屏；回落重置保证恢复后的新积压有即时信号
- 测试：`BacklogWarning_WhileOverThreshold_IsRateLimited` / `BacklogWarning_AfterRecovery_WarnsImmediatelyAgain`

**Q5.4 单轮异常兜底**
- `catch (Exception ex) when (ex is not OperationCanceledException)` → LogError 后本轮结束、下轮继续，引擎不退出
- OCE 不吞 → 向上冒泡交给停机路径
- 注意：这是引擎级兜底（scope 创建 / ForwardBatchAsync 意外异常），Forwarder 内部已有细粒度失败路径

**Q5.5 优雅停机**
- Host 停止 → stoppingToken 取消 → `WaitForNextTickAsync` 抛 OCE → `ExecuteAsync` 捕获并记 "ForwarderEngine Stopped." → BackgroundService 正常结束
- 与「单轮异常」catch 的区分：OCE 仅在停机时吞，其余异常记日志后继续

---

## 六、序列化与 Topic

**Q6.1 序列化**
- `static readonly JsonSerializerOptions`：线程安全可复用，避免每次序列化重复创建配置
- `PropertyNamingPolicy = CamelCase`：与前端/云端 JSON 约定一致
- `Encoding.UTF8.GetBytes`：输出 UTF-8 字节；`ContentType = "application/json"`

**Q6.2 Topic 模型**
- 每设备一个 Topic `nitrogateway/{deviceId}/measurements`：云端按设备订阅/路由，直观但粒度粗
- 问题：设备规模大时订阅/过滤成本高、权限粒度粗、消息体积无法按点位类型区分
- v2 多级 Topic / 按点级别（如 `nitrogateway/{site}/{device}/{pointType}`）：订阅更精细、路由更灵活（DESIGN.md v1 决策表 Topic 行）

---

## 七、可观测性

**Q7.1 指标**
- `ForwardTotal`（labels: success/failure）：转发成功/失败批次数，增量 Counter
- `BufferBacklog`：待转发 Pending 批数，存量 Gauge（`_buffer.Count`，同步兼容接口）
- `ThrottleBatchSize`：当前节流批量上限，暴露「系统是否处于节流/恢复」状态，运维可区分正常恢复与异常
- Activity "Forward"：每轮一次，状态 Ok/Error + BatchSize / ErrorMessage 标签（ADR-001 P2-9）

**Q7.2 断线 3 小时时间线**
1. 断线瞬间（已连接但发布失败）：throttle 收紧（1000→500→…→100，延迟→200ms）；`ForwardTotal{failure}` 上升
2. 持续断线（Transport 层状态变 Disconnected/Reconnecting）：引擎跳过本轮，不 Dequeue、不产生死信；积压 >1000 → 首次 Warning，之后每 60s 一次
3. 期间：`BufferBacklog` 持续增长（Collection 仍写入）；无死信产生（未发布不累加失败计数）
4. 恢复：引擎放行 → throttle 从 100/200ms 缓慢回升（成功 +10 / -5ms），分多轮排水；积压回落 → 告警状态重置；`ForwardTotal{success}` 陡增
5. 若恢复后发布仍持续失败（如认证/ACL 错误）：retry_count 累加，5 次后进死信 → 死信 API 可见

**Q7.3 恰好一次？**
- 不能保证。QoS1 = 至少一次（Broker 可能收到但 ACK 丢失 → 重发）；本地 Commit 时机是「发布成功」而非「消费成功」；崩溃窗口会重发
- 业务兜底：云端按 BatchId / Record.Id 幂等去重；v4 应用层 ACK 也只是「消费确认」，仍需要幂等

---

## 八、开放性 / 演进

**Q8.1 演进排序理由**
- v2（Protobuf + 多 Topic）：数据量与设备规模是首要瓶颈，带宽与订阅粒度收益直接（当前 JSON 文本 + 单设备 Topic）
- v3（双通道 MQTT+HTTP）：网络环境复杂时才需要，成本高
- v4（应用层 ACK + 自适应退避）：需要确认消费才做，协议复杂度大
- v5（批量合并 + 压缩 + 蜂窝优化）：带宽敏感场景
- 排序逻辑：收益/成本比 + 触发条件（演进表注明了「数据量大时 / 网络环境复杂 / 需要确认消费 / 带宽敏感」）

**Q8.2 应用层 ACK**
- 设计：云端消费成功后回执（专用 ack Topic，消息携带 BatchId）；Forwarder 收到回执才 Commit；未确认超时重发
- 与现状差异：Commit 时机从「发布成功」→「确认消费」；需要订阅回执 Topic、InFlight 超时/重发策略、回执幂等
- 代价：双 Topic、状态追踪变复杂、ack 丢失需处理、云端必须实现回执
- 本质：把至少一次推进到「至少一次 + 消费确认」，仍非恰好一次（仍需幂等）

**Q8.3 容量策略**
- 现状：缓冲无条数上限，只受磁盘约束；有积压告警（1000 批 / 60s）但无止损
- 方向：① 磁盘水位告警；② 缓冲条数上限 + 拒绝策略（工业场景「不丢」优先，需权衡）；③ TTL 老化（超时未转发进死信并告警）；④ 死信保留期 / 自动清理；⑤ 采集侧联动（积压超限暂停采集或降频）
- 原则：容量策略要可配置、默认保守，不破坏 at-least-once

**Q8.4 坏消息分类**
- 问题：连接类错误（Broker 不可达，重试合理）与数据类错误（格式错误、超大 payload，重试必然失败）走同一路径 → 浪费 5 轮重试、且每轮都 `OnMqttFailure` 收紧全局节流，拖慢正常数据
- 优化：错误分类——不可恢复错误（序列化/数据问题）直接进死信 + 告警；连接类错误走重试；序列化失败发生在本地，可立即死信
- 现状已隐含演进方向（v2+ 自适应退避、应用层 ACK），v1 未区分错误类型
