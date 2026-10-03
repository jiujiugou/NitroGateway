# ADR-078: 驱动双接口释放（同步不排水 / 异步优雅拆除）

> **摘要**: `IProtocolDriver` / `IProtocolDriverPool` 同时实现 `IDisposable` 与 `IAsyncDisposable`。同步 `Dispose` 保持 ADR-077 的「不排水」语义（幂等、不抛、不等在途）；异步 `DisposeAsync` 做「等本驱动闸门」的优雅拆除——因不阻塞线程，不会触发 ADR-077 的宿主 UI 死锁，从而让之前被否的排水在异步路径上重新可行。池新增 `EvictAsync`。

- 日期: 2026-10-01 | 状态: 已实施
- 来源: ADR-077 备选 C（`IAsyncDisposable`「暂拒，若未来跨宿主调度需求增强可重估」）重估；`notes/Invariants/modbus.md` X1/X2

## Context

ADR-077 拒绝了「排水」（Dispose 等待在途调用），根因是**同步** `Dispose` 在 UI 线程阻塞等待一个需要 UI 线程才能完成的在途续体 → 互等死锁。于是同步路径只能「不排水」，留下：

- Modbus RTU `Dispose` = `DisconnectAsync().GetAwaiter().GetResult()`（sync-over-async，X2）；
- Modbus TCP `Dispose` 绕过 `Gate`，与在途读并发（X1）；
- OPC UA `Dispose` 内亦有 `.GetAwaiter().GetResult()`。

这些是**同步接口**与**异步生命周期**不匹配的产物，而不是业务缺陷。

## Decision

- **D1 双接口**：`IProtocolDriver : IDisposable, IAsyncDisposable`；`IProtocolDriverPool : IDisposable, IAsyncDisposable`。接口提供**默认实现**，未显式实现异步的替身（测试 fake）退化为同步 `Dispose`（零改）。
- **D2 同步 = 不排水（不变）**：`Dispose()` 尽力而为、不等待闸门/在途操作，幂等、不抛（ADR-077 原语义）。
- **D3 异步 = 优雅拆除**：`DisposeAsync()` 在本驱动闸门保护下拆除，**等待**在途操作完成；不阻塞线程，故不产生 ADR-077 死锁。
- **D4 幂等位共用**：同一实例同步/异步释放共用一个 `Interlocked` 幂等位，先到先得、后到 no-op。
- **D5 基类收口**：`ModbusDriverBase` 拥有 `Dispose`/`DisposeAsync` 模板与 `_disposed`，子类只实现 `DisposeCore()`（同步）与 `DisposeAsyncCore()`（异步，默认回退同步）。
- **D6 不释放闸门**：拆除**不** `Dispose` `SemaphoreSlim`（`_readLock`/`_sync`/`Entry.Gate`）——异步 `WaitAsync` 不创建 OS 句柄，泄漏可忽略；主动释放反而会让在途等待者 `ObjectDisposedException`。顺带移除 S7 / OPC UA 原有的 `_gate.Dispose()`（决策统一）。
- **D7 新增 `EvictAsync`**：`Evict` 保持同步（不排水）；`EvictAsync` 走驱动异步释放，供 `DeviceManager`、`CircuitBreakerHealthListener`、`OpcUaCertificatesController` 等异步调用点优雅下线。
- **D8 调用点**：`await using`（连接测试）与宿主关停（桌面 `GatewayHost`/`App` 已 `DisposeAsync`）自动走异步优雅路径。

## Alternatives

- **A 只改具体驱动的 `Dispose`**：接口是同步的，池仍调 `driver.Dispose()`，收益为零。拒。
- **B 全量异步（去 `IDisposable`）**：`Evict`（事件回调）等同步调用点需全部异步化；且 DI 中只实现 `IAsyncDisposable` 的同步 `Dispose` 会抛异常。成本高、风险大。拒（保留双接口渐进）。
- **C 维持 ADR-077 现状**：X1/X2 与 OPC UA 的 sync-over-async 无法在同步接口内安全消除。被本 ADR 取代其「暂拒」结论。

## Rationale

- ADR-077 的约束条件是「**同步**阻塞 + 宿主上下文」，异步释放不满足该条件，因此不是背离 ADR-077，而是**补齐其留白**：同步路径原样、异步路径拿到之前拿不到的正确性。
- 默认接口实现让 15+ 测试替身零改；真实驱动显式覆写。
- 与 ADR-074（单闸门所有权）一致：异步拆除复用各自闸门，而非新增并发原语。

## Consequences

- RTU 的 sync-over-async 消除（异步路径）；TCP 的 Dispose-与在途并发在异步路径消除（X1 收窄为「仅同步路径」）。
- `IProtocolDriver` 释放契约改写为同步/异步两条；`IProtocolDriverPool` 增 `EvictAsync`。
- 检测器：`ProtocolDriverDisposalTests`（装饰器异步委托、跨接口幂等、释放后快速失败、默认回退、池 EvictAsync/DisposeAsync）。
- 仍**未**处理：RTU `Gate` 移动引用 / 换租约窗口（X3，与释放无关）；全链路缺 `ConfigureAwait(false)`（ADR-077 遗留前提）。
