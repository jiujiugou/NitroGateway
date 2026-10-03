# Collection 流水线并发模型（Engine / WriteHost / Retention）

- 目标：`src/NitroGateway.Collection/CollectionEngine.cs`、`Dispatcher/MeasurementWriteHost.cs`、`src/NitroGateway.Persistence/Sqlite/MeasurementRetentionService.cs`
- 检测器：`tests/NitroGateway.CoyoteTests/Collection/CollectionEngineInvariants.cs`、`Collection/ChannelHostInvariants.cs`、`Storage/RetentionInvariants.cs`
- 日期：2026-10-03

## 范围与非目标

**覆盖**：宿主关停路径上的共享生命周期状态——引擎当前轮 `_currentRound/_roundCts` ↔ `StopAsync`；写宿主 Channel 关闭/排空 ↔ 生产端 `Post`；保留清理的 cutoff 与停机取消。

**非目标**：`DeviceCollector` 采集语义、驱动/协议层（见 `modbus.md`/`protocol-driver-pool.md`）、`ChangeDetector` 纯函数、`SinkDispatcher` 的 Sink 调用（其关闭语义与 WriteHost 同型，未单独检测，列为 X4）。

## C 行为契约

| 项 | 内容 |
|---|---|
| 目的 | 引擎按周期驱动全量采集；写宿主异步落库；保留服务周期清理 |
| 生命周期 | 均为 `BackgroundService`；宿主 `StartAsync`/`StopAsync` 驱动 |
| 使用期归属 | 引擎每轮独立 DI scope；写宿主 Channel 由生产者（`DataDispatcher`）`Post` |
| 调用方义务 | 生产者在宿主停止后不得再 `Post`（宿主必须拒绝，不得静默接收） |

## I 不变量

| # | 不变量 | 检测器（正例绿 / 负控红） |
|---|---|---|
| I1 | **停后不再启新轮**：`StopAsync` 返回后引擎已静止，不再启动采集轮 | 正：真实引擎，`StopAsync` 后 `CollectOnceAsync` 调用数不再增长；负：忽略停止令牌的坏引擎 → 停后继续增 |
| I3 | **关停后 Post 必须被拒**：宿主停止后 `Post` 返回 `false`，不得静默接收后丢弃 | 正：真实写宿主（修复后 `TryComplete`）→ `false`；负：不关闭写入端的坏宿主 → `true` |
| I4 | **保留 cutoff 恒为 `now-retentionDays`**：清理只删窗口外数据，且停机取消不启动新清理 | 正：真实保留服务，首轮 cutoff≈now-30d、停后无新清理；负：cutoff=now 的坏服务 → 断言红 |

## X 豁免（明知做不到、决定接受）

| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | 引擎轮次耗时长于间隔时的堆积 | `PeriodicTimer` 重新计时，设计上不堆积 | 单轮超时则采集频率下降 | 契约已声明 |
| X2 | Channel 满时 `DropOldest` 丢数据 | 有界 Channel 的显式设计 | 高负载丢最旧批 | 设计决策（非本工作引入） |
| X3 | 停机排空 5s 超时后丢弃余量 | 防止慢存储拖死停机 | 停机瞬间丢尾批 | `DrainTimeout` 常量 |
| X4 | `SinkDispatcher` 未单独检测 | 与 WriteHost 同型；其 `Dispose` 已 `TryComplete` | 同 I3 | 复用 I3 结论 |
| X5 | `DiskGuardService.Level` 并发 `CheckOnce` | 生产由单后台循环驱动，非并发调用 | 无 | 单循环假设 |
| X6 | 在途轮收敛（`StopAsync` 等待/取消在途轮）无检测器 | 真实引擎内 `Task.Delay(30s)` 超时路径在 Coyote 受控时间下于部分调度被判 deadlock（假阳性，非真实缺陷），隔离跑可过 | 该保证无系统性验证 | I1 已覆盖「停后静止」；如需可改引擎超时为可注入 seam 后再补 |

## 同步规则

- 引擎：`_currentRound/_roundCts` 由循环写、`StopAsync` 读；停止用 `Task.WhenAny` 带 30s/5s 虚拟超时，取消包 `ObjectDisposedException`。
- 写宿主：单读者循环消费 Channel；停止令牌取消后进入有限排空，**随后必须关闭写入端**（I3 修复）。
- 保留：`ExecuteAsync` 顺序 `PurgeOnceAsync` → `Task.Delay(interval, token)`；取消即退出。

## 变更记录

| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿 + I1–I4 检测器；发现并修复 WriteHost 关停后仍接收 Post（I3） | /并发建模 |
