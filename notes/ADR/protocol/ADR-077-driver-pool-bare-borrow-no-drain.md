# ADR-077: 驱动池裸借用：不做生命周期排水（释放不等待在途调用）

> **摘要**: `IProtocolDriverPool` 按裸借用返回驱动；`ReliableProtocolDriver.Dispose` 不等待在途调用（去掉引用计数排水），释放后到达的调用快速失败、由调用方按可恢复错误处理。明确拒绝"排水"——它会在带 `SynchronizationContext` 的桌面宿主造成 UI 死锁。

- 日期: 2026-09-27 | 状态: 已实施
- 来源: ADR-074（装饰器不承担并发原语）；并发模型 `notes/Invariants/protocol-driver-pool.md` 的 X1；尝试"排水"修复后回退

## Context

设备驱动池按 `device.Id` 复用长连接并借出驱动。`Evict` / `Dispose` / 连接指纹变化（换键）可在调用方使用期间释放驱动，形成 use-after-dispose 窗口（并发模型 X1）：

- 触发源：`DeviceManager`（注册/注销/软删/状态变更）、`CircuitBreakerHealthListener`（离线）、`OpcUaCertificatesController`（信任证书）、宿主关停；
- 后果：在途 Read/Write/Browse 调用撞上已释放的内层客户端 → 崩溃或偶发 500。

曾尝试消除该窗口：在装饰器 `ReliableProtocolDriver` 加 **引用计数排水**——每个公开方法进入 +1、退出 −1，`Dispose` 置位后等待计数归零再拆内层。该实现的内存可见性与排空逻辑本身正确（有 G1/G2 检测器），但与宿主模型冲突。

根因：仓库全链路**无 `ConfigureAwait(false)`**，所有 `await` 捕获当前 `SynchronizationContext`。桌面（WPF）宿主下：

- `RealtimeViewModel` 写值命令在 UI 线程 `await WriteService.WriteAsync` → 驱动链 `await` 捕获 UI 上下文，其续体（含排水信号）排回 UI 线程；
- `DeviceManager.RegisterAsync` 等 `await` 续体亦在 UI 线程，随后同步调用 `Evict → Dispose`；
- 于是 `Dispose` 在 UI 线程**阻塞等待**一个同样需要 UI 线程才能完成的在途续体 → **互等死锁**（至少是 UI 冻结）。

## Decision

- **D1 保持裸借用**：`IProtocolDriverPool` 不引入租约 / 引用计数；借出的驱动仍可在调用方使用期间被释放。
- **D2 不做生命周期排水**：`ReliableProtocolDriver.Dispose` 不等待在途调用——`Interlocked.Exchange` 置 `_disposed` 后直接 `_inner.Dispose()`。
- **D3 释放后快速失败**：装饰器每个公开方法入口检查 `_disposed`，已释放则返回 `OperationResult` 失败，**不触达内层、不抛异常**。
- **D4 调用方兜底**：调用方把"释放与在途调用并发"导致的失败当可恢复错误处理（`WriteService`、`OpcUaBrowseController` 已加 try/catch）。

## Alternatives

- **A 引用计数排水**（已实现后回退）：能彻底关闭 use-after-dispose 窗口；但在无 `ConfigureAwait(false)` 的宿主下，同步阻塞 `Dispose` 会与在途续体互等 → 桌面 UI 死锁。**把低频可恢复的失败换取低频不可恢复的死锁，负收益**，拒。
- **B 显式 `IProtocolDriverLease`（调用方负责释放）**：订阅等长持有不匹配；漏放导致 socket 永久泄漏，比现状更糟。拒。
- **C `IAsyncDisposable`**：语义正确，但需改接口与全部调用点；收益不抵成本，暂拒（若未来跨宿主调度需求增强可重估）。

## Rationale

- 按 **爆炸半径 × 频率 × 后果可逆性** 取舍：原问题低频、单次、可恢复；排水把后果升级为不可逆冻结。
- 与 **ADR-074** 一致：装饰器只承担可靠性横切（超时 / 重试 / 自动建连），**不承担并发生命周期原语**。
- "释放后快速失败 + 调用方兜底"已消除崩溃 / 500，保留的仅是一次可恢复失败，风险可接受。

## Consequences

- `ReliableProtocolDriver`：删除 `_inFlight` / `_drained` / `Enter` / `Exit`；保留 `_disposed` 幂等与快速失败检查。
- `IProtocolDriver` 释放契约措辞更新：**不承诺** `Dispose` 等待在途；释放后调用必须返回失败、不抛异常。
- 检测器 `ReliableProtocolDriverConcurrencyTests`：保留 G2（释放后干净失败）并配负控 `NoGuardWrapper`；新增 R1（`Dispose` 不阻塞在途）并配负控 `DrainWrapper`，**防止排水被重新引入**。
- 豁免 X1 保留于 `notes/Invariants/protocol-driver-pool.md`，并记录"曾尝试排水、因 UI 死锁回退"。
- **遗留（另案）**：全链路缺 `ConfigureAwait(false)` 使驱动调用绑定宿主上下文。本 ADR 不处理，但它是本决策成立的前提；若未来统一改为 `ConfigureAwait(false)`，可重估是否重新引入排水。
- 桌面 UI 线程上的 `Evict → Dispose` 仍会同步执行 `_inner.Dispose()`（含 socket 关闭），可能短暂卡 UI；不在本 ADR 范围。
