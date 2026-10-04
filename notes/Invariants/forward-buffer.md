# ForwardBuffer（SqliteForwardOutbox）并发模型

- 目标：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs`
- 检测器：`tests/NitroGateway.CoyoteTests/Storage/ForwardBufferInvariants.cs`
- 日期：2026-10-03

## 范围与非目标

**覆盖**：多执行流（采集 `DataDispatcher.Enqueue` ↔ 转发 `ForwarderEngine`/`HttpForwarderEngine` 的 Dequeue/Commit/MarkFailed ↔ 启动恢复）并发访问同一 SQLite 库时的**托管层逻辑竞态**。

**非目标**：SQLite 引擎内部锁、磁盘 IO、断电原子性（由 WAL/事务保证，不在此建模）；`MaxDrainPerRound` 限流策略。

## C 行为契约

| 项 | 内容 |
|---|---|
| 目的 | 断电不丢的 FIFO 转发队列：Enqueue(Pending) → Dequeue(Pending→InFlight) → Commit(删) / MarkFailed(回 Pending 或超限删) |
| 生命周期 | 单例进程内共享；首用时 `EnsureRecoveredAsync` 把上次残留 InFlight 重置为 Pending（只一次） |
| 使用期归属 | 调用方各自持短连接/事务；接口返回 `OperationResult`，不抛（除取消） |
| 调用方义务 | 出队后必须 Commit 或 MarkFailed；不得假设 Dequeue 结果永久有效 |

## I 不变量

| # | 不变量（人话：无论几路并发都必须成立） | 检测器（正例绿 / 负控红） |
|---|---|---|
| I1 | **同一批不会被两路同时取走**（出队即占位 InFlight） | 正：真实 outbox 并发 `DequeueAsync` → 汇总 id 无重复；负：非原子出队坏缓冲 → 重复 |
| I2 | **提交成功后批次不再出现**（Commit 物理删除，且只删 InFlight） | 正：出队→Commit→GetCount=0 且再出队为空；负：Commit 不删/改回 Pending → 仍可出队 |
| I3 | **InFlight 必收敛**：MarkFailed 未超限回 Pending、超限删除；永不残留 InFlight | 正：出队后连续 MarkFailed 至上限 → 无 Pending、无 InFlight 残留；负：MarkFailed 不重置状态 → InFlight 残留 |
| I4 | **背压不超上限**：并发 Enqueue 后 Pending ≤ maxPending（check-then-act 必须原子） | 正：maxPending=2、10 路并发 Enqueue → 计数 ≤2；负：check-then-act 坏缓冲 → 超限 |

## X 豁免（明知做不到、决定接受）

| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | SQLite 内部锁 / 跨进程并发 | 测试只插桩托管层，不建模 sqlite3 原生锁 | 多进程写同库的 `SQLITE_BUSY` | 单进程单例使用（接口注明进程内共享） |
| X2 | `Count` 属性（同步）不做恢复、不归类异常 | 类注释已声明，async 路径用 `GetCountAsync` | 首次同步读可能看到未恢复状态 | 调用方仅用于非关键统计 |
| X3 | 单条 MarkFailed 与并发 Commit 对同一 id 的竞争结果 | 两操作都带 `status`/`retry_count` 条件，DB 串行化最终态一致 | 极端下重复投递 | at-least-once 语义，允许重复不允许丢 |

## 同步规则

- 临界区：启动恢复由 `_recoveryGate`(SemaphoreSlim) 单飞；状态迁移由每操作的独立事务 + `WHERE status=...` 条件保证。
- 释放：`Dispose` 仅释放 `_recoveryGate`（Singleton，宿主关闭）。
- **I4 结论**：`EnqueueAsync` 的「查 Pending 计数 + INSERT」是跨两条独立语句的 check-then-act；检测器在多轮探索下**未复现**超限（SQLite 写锁 + busy_timeout 掩盖了窗口），故**未改 src**，保留为监控不变量。负控（`BrokenBuffer`）证明检测器有能力失败。

## 机制选择

| 候选 | 失败时序 | 结论 |
|---|---|---|
| 现有：独立语句 check-then-act（连接级 busy_timeout 串行写） | 理论两路同时读到 count=max-1 → 双插 | 检测器未复现；保留监控 |
| `BEGIN IMMEDIATE` 事务包住 count+insert | 第二路等写锁，重读 count 后拒绝 | 若将来复现可采用的加固 |
| 单语句 `INSERT ... SELECT WHERE (SELECT COUNT) < max` | 原子但可读性差 | 备选 |

## 变更记录

| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿 + I1–I4 检测器；I4 在多轮探索下绿、未改 src | /并发建模 |
| v2 | 追加 F1–F3：`Forwarder` 有界并发发布（客户端侧，非 buffer 内部） | 转发吞吐优化 |

## 附：Forwarder 有界并发发布（客户端侧）

- 目标：`src/NitroGateway.Forwarder/Forwarder.cs`（`ForwardBatchAsync`）
- 背景：串行 QoS1 发布被公网 RTT 卡死（吞吐 ≈ 1/RTT），改为单轮在途发布数 ≤ `Forwarder:MaxConcurrentPublishes`（默认 8，夹紧 [1,64]）。buffer 内部（I1–I4）不变，仅调用方式由串行改为有界并发。
- 检测器：`tests/NitroGateway.UnitTests/Forwarding/ForwarderTests.cs`（有界并发/串行退化/部分失败/不重复发布）。

| # | 不变量（人话） | 检测器 |
|---|---|---|
| F1 | 同一批只发布一次（每个 batch 由唯一 worker 处理，committed 记其 Id 唯一） | `ForwardBatchAsync_Concurrent_DoesNotPublishDuplicateBatches` |
| F2 | 在途发布数 ≤ MaxConcurrentPublishes（SemaphoreSlim 闸门） | `ForwardBatchAsync_BoundedConcurrency_NeverExceedsConfiguredDegree` / `..._MaxConcurrentPublishesOne_IsSerial` |
| F3 | 取消时不 Commit（已出队未提交批次靠启动恢复重置 InFlight，与 v1 I3 一致） | `ForwardBatchAsync_OperationCanceled_Rethrows` / `..._CancelledBeforeCommit_DoesNotCommit` |
| F4 | 并发下部分失败：成功批次全提交、失败批次全 MarkFailed（容器线程安全） | `ForwardBatchAsync_ConcurrentPartialFailure_CommitsSuccessesMarksFailures` |

**豁免**：发送顺序不保证（并发发布 + QoS1 PUBACK 乱序）；遥测带时间戳，消费端须容忍乱序。`Activity` 非线程安全 → worker 内不改 activity，收尾统一设置（已在实现中保证）。

**非目标**：MQTTnet/Polly 内部并发、Broker 端排序、`MaxDequeuePerCall` 批量上限。
