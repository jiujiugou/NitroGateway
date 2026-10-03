# GatewayLifecycle 并发模型

- 目标：`src/NitroGateway.Host/GatewayLifecycle.cs`
- 检测器：`tests/NitroGateway.CoyoteTests/Host/GatewayLifecycleInvariants.cs`
- 日期：2026-10-03

## 范围与非目标

**覆盖**：采集侧（`CollectionEngine`）与转发侧（`ForwarderEngine`）跨模块关闭握手时，两个生命周期标志的**单调性与并发安全**。

**非目标**：关闭顺序编排本身（`Host` 的 `StopAsync` 顺序）、`ForwarderEngine` 的排空超时策略。

## C 行为契约

| 项 | 内容 |
|---|---|
| 目的 | 协调关闭：采集 `RequestStop` → 转发据 `IsDraining/IsStopped` 决定何时排空 |
| 生命周期 | 单例；被两个 BackgroundService 跨模块读写 |
| 使用期归属 | 调用方只调 `RequestStop`/`MarkStopped`；只读 `IsDraining`/`IsStopped` |
| 调用方义务 | 不得回退标志（无复位 API） |

## I 不变量

| # | 不变量 | 检测器（正例绿 / 负控红） |
|---|---|---|
| I1 | **单调不可回退**：任一标志一旦为 true 永不为 false | 正：并发 `RequestStop`/`MarkStopped`/读，终态两标志皆 true 且读到的 true 不回退；负：会翻转（toggle）的坏状态 → 终态非 true |

## X 豁免（明知做不到、决定接受）

| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | `IsStopped=true` 一定蕴含 `IsDraining=true` | 类未强制该顺序，由调用方（引擎先 RequestStop 再 MarkStopped）保证 | 乱序调用者会读到非法组合 | 引擎调用顺序（`CollectionEngine.StopAsync`） |

## 同步规则

- 临界区：三个 setter/getter 全部 `lock (_lock)`；无锁外可见状态。
- 释放：无资源，不实现 IDisposable。

## 变更记录

| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿 + I1 检测器 | /并发建模 |
