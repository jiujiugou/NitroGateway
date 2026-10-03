# OpcUaDriver 会话自愈并发模型（Coyote 版）

- 目标文件：`src/NitroGateway.Protocol/OpcUa/OpcUaDriver.cs`
- 检测器：`tests/NitroGateway.CoyoteTests/Protocol/OpcUa/OpcUaDriverInvariants.cs`
- 用例注册：`tests/NitroGateway.CoyoteTests/Program.cs`（`[OpcUa]` 6 例：3 正例 + 3 负控）
- 日期：2026-10-03
- 依据：ADR-072（会话自愈 D1–D6）、ADR-077/078（释放语义）

## 0. Coyote 运行前提
| 项 | 内容 |
|---|---|
| 被 rewrite | `NitroGateway.*`（含 `NitroGateway.Protocol.OpcUa`） |
| **不可控外部** | OPC Foundation SDK（`Session`/`Subscription`/`SessionReconnectHandler`）→ 需网络与证书，**不进调度器**；必须挡在接缝后 |
| 入口接缝 | `OpcUaDriver.HandleKeepAliveBad(object? session, ServiceResult? status)`（SDK 无关） |
| 测试缝 | `CurrentSessionOverrideForTesting` / `StateOverrideForTesting` / `ReconnectStarterOverrideForTesting`（生产为 null，行为不变） |
| 暴露 | `NitroGateway.Protocol.OpcUa.csproj` 加 `InternalsVisibleTo NitroGateway.CoyoteTests` |

## 1. 范围与非目标
**覆盖**：KeepAlive Bad → 自愈抢占（D3 防重入）/ 分类门控（D2/D6：当前会话、Connected、Good）/ D5 自愈窗口内失败读不置 Faulted。

**非目标**：真实会话读写/Browse/订阅的闸门串行（需 SDK，见 `IntegrationTests`）；`OnReconnectComplete` 重连完成与订阅迁移（需真实 `SessionReconnectHandler`）；建连/证书/安全参数（集成测试覆盖）。

## 2. 行为契约（C）
| 项 | 内容 |
|---|---|
| 目的 | “已连接后的断线”由 KeepAlive→自愈接管；初始建连/主动重连由上层 `ReliableProtocolDriver` 负责，二者按 `DriverState` 分工不抢道 |
| 生命周期 | 长连接驱动由 `ProtocolDriverPool` 按设备复用；`ConnectAsync` 幂等；`Dispose`/`DisposeAsync` 幂等 |
| 使用期归属 | 所有会话操作经 `_gate` 串行；KeepAlive 回调运行在 SDK 线程，只用有界等待（2s/5s）取闸门 |
| 调用方义务 | 不得在回调内长时间持锁；自愈只接管 Connected 状态 |

## 3. 不变量（I）
| # | 不变量 | 检测器（正例绿 / 负控红） |
|---|---|---|
| I1 | **D3 抢占单胜者**：并发多路 Bad，恰一路胜出启动自愈；防重入位抢占后粘滞 | 正：`HandleKeepAliveBad` 并发 8 路 → starts==1 且 `IsReconnectActiveForTesting`；负：无闸门 check-then-act 坏闩锁 → starts>1 |
| I2 | **D2/D6 分类门控**：仅“当前会话 + Connected + Bad”可胜出；旧会话/Good 恒不启动 | 正：当前会话/旧会话/Good 混杂并发 → starts==1；负：忽略身份与防重入 → starts>1 |
| I3 | **D5 窗口内失败读不置 Faulted**：自愈窗口开启后，失败读保持原状态（避免与上层整轮重建抢道） | 正：开窗后并发 `EnterFaultedIfNotSelfHealing` → State≠Faulted；负：忽略窗口无条件置 Faulted → State==Faulted |

## 4. 同步规则
- `_gate`（`SemaphoreSlim(1,1)`）：保护 `_session`/`_subscription`/`_reconnectHandler`/`State` 及抢占+启动全过程；**启动 `SessionReconnectHandler` 与原子置位在同一闸门临界区内**，顺序不可反（否则 Disconnect/Dispose 可与启动抢会话，违背 D6）。
- `_reconnectActive`（0/1）：快速路径 `Volatile.Read` 免争闸门；正式置位/复位用 `Interlocked`（闸门内）。

## 5. 豁免（X）
| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | `State` 非 volatile | 所有写都在 `_gate` 内（`EnterFaultedIfNotSelfHealing` 由读路径在闸门内调用；测试替身无并发语义） | 若未来把 State 写入移出闸门 → 可见性/丢写 | 代码评审 + 本模型 §4 |
| X2 | 真实会话闸门串行未由 Coyote 覆盖 | `Session` 非线程安全且需网络，无法在调度器内构造 | 读写/Browse/订阅并发回归 | `IntegrationTests` + ADR-074 单闸门模板 |

## 6. 变更记录
| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿：自愈缝（`HandleKeepAliveBad` + 3 测试替身）+ I1/I2/I3 检测器 | /并发建模（OpcUaDriver） |
