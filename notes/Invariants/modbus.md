# Modbus 驱动 并发模型

- 目标文件：`src/NitroGateway.Protocol/Modbus/`（`ModbusTcpDriver` / `ModbusRtuDriver` / `SerialPortManager` / `ModbusDriverBase`）
- 日期：2026-09-27
- 范围：单闸门串行、RTU 同端口串口共享与租约引用计数、换租约窗口、Dispose 边界。
- 非目标：`ModbusAddressParser` / `ModbusBatchPlanner` / `ModbusAddress` / `ModbusArea` / `ModbusDriverCapability`（无状态纯函数）；HslCommunication 客户端内部线程安全（外部库）；装饰器 `ReliableProtocolDriver`（见 `protocol-driver-pool.md` / ADR-077）；上层池与调用方。

## C 行为契约（这个类答应怎么用）
| 项 | 内容 |
|---|---|
| 目的 | Modbus TCP/RTU 驱动的连接/读/写/Ping；RTU 经 `ISerialPortManager` 复用同端口串口 |
| 前置 | `_connection` / `_settings` 构造后只读；`ISerialPortManager` 线程安全（Singleton）；`Gate` 有效 |
| 行为 | 公开读/写/Ping/Connect/Disconnect 走单闸门（`Gate`，ADR-074）；RTU 换租约在 `_sync` + 旧共享 `Gate` 下做「Dispose 旧 → Acquire 新」；`Dispose` 幂等 |
| 副作用 | 可能开关串口（最后一个租约释放时关闭）；改写共享 `Rtu.Station` |
| 生命周期 | TCP `_client` 每驱动一份；RTU 的 `ModbusRtu` **按端口共享**、引用计数、最后租约释放时关闭 |
| 使用期归属 | **裸借用**（池借出装饰器，ADR-077）；驱动**同步** `Dispose` **不保证**与在途帧并发安全（见 X1）；**异步** `DisposeAsync` 取闸门，优雅拆除（ADR-078） |
| 调用方义务 | 优先 `await DisposeAsync`（优雅）；不得依赖同步 `Dispose` 等待在途（X2）；不得绕过闸门直接操作底层客户端 |

## I 不变量（必须永远成立）
| # | 不变量 | 检测器（红） | 豁免/边界 |
|---|---|---|---|
| I1 | 同一驱动实例的受保护操作（Connect/Disconnect/Read/Write/Ping）对底层客户端**串行**（单闸门） | 已有 `ProtocolDriverGateConcurrencyTests`：显式 Connect 与读触发建连最大并发==1；Coyote `ModbusDriverBaseInvariants.I1_GateSerialization_*`（6 入口混合并发 + 无闸门/仅 Ping 绕过 负控） | — |
| I2 | RTU 同端口多从站**帧级串行**：每次通信在共享 `Gate` 内先切 `Station` 再发帧 | 部分：Coyote `SerialPortInvariants.I2I3_Positive` 断言同端口共享同一 `Rtu`/`Gate`（串行前提）；"帧内 `Station` 与发送者一致"需抽象 `ModbusRtu`（**非目标**） | 依赖共享 Gate 有效 |
| I3 | 串口租约引用计数账平：同端口 N 个租约，**最后一个 Dispose 才关闭并移除**；异参 Acquire 抛异常 | Coyote：`SerialPortInvariants.I2I3_Positive`（Acquire×3→计数 3/2/1/0，仅开一次）+ `I3_ParamMismatch_Throws`；**驱动级** Coyote `ModbusRtuDriverInvariants.I3_LeaseAccounted_*`（并发 Connect/Disconnect/Dispose 后无残留租约、每串口恰释放一次 + 只取不还 负控） | — |
| I4 | **换租约不撕裂**：Connect 替换 `_lease` 时，不得与在途帧并发使用旧句柄 | 待写：受控交错——替换期间并发读不得进入旧句柄/旧闸门混用；需假 `ModbusRtu` 抽象（**非目标**） | 见 X3（残余窗口待定） |
| I5 | 每个 `SerialPortLease` 恰 Release 一次（`_disposed` Interlocked） | Coyote：`I5_ConcurrentDispose_ReleasesOnce`（8 并发 Dispose 只归还一次，已自动化；**无负控**——`SerialPortLease` sealed 无法注入坏实现） | — |
| I6 | 驱动同步/异步释放共用幂等位（`ModbusDriverBase` 模板收口 `_disposed`），并发/跨接口释放只拆一次 | `ProtocolDriverDisposalTests`（`BaseDriver_Dispose_IsIdempotent_AndSharesFlagWithAsync` 等）；Coyote `ModbusDriverBaseInvariants.I6_DisposeIdempotent_*`（并发同步/异步释放 + 无幂等位 负控） | — |

## X 豁免（明知接受、写理由+风险）
| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | TCP **同步** `Dispose()` 直接 `_client.Dispose()`（不取 `Gate`），可与在途读并发 → 在途读撞已释放 `_client` | 同步路径保持 ADR-077 不排水；与 ADR-077 X1 同族（裸借用） | 在途读可能失败/异常（`DeviceReader` 已兜底为一次失败） | 异步路径 `DisposeAsync` 取 `Gate`：Coyote `ModbusTcpDriverInvariants.X1_AsyncDisposeDrains_*`（等在途读完成才拆 + 不排水 负控） |
| X2 | RTU **同步** `DisposeCore()` 直接 `_lease?.Dispose()`，**不等** `_sync`/共享 `Gate`（不排水） | ADR-078 D2：同步不排水，换取不阻塞/不死锁 | 可能与在途帧并发（同 X1 族）；**曾因清空顺序泄漏租约**（见 v5） | 异步 `DisposeAsync` 走 `DisconnectAsync`（等闸门）：Coyote `ModbusRtuDriverInvariants.X2_AsyncDisposeDrains_*`；同步清空改 `Interlocked.Exchange`（原子置空） |
| X3 | 换租约窗口内 `Gate` 在 IO 入口读取、`Rtu` 在用时读取，可能「旧共享闸门 + 新句柄」混用（`cs:81-82` 与基类 `Gate` 之间） | 依赖 `_sync` + 旧闸门约定（`cs:77-90`） | 潜在帧交错（低概率，随重连触发） | 待评估（是否纳入 I4 检测器） |
| X4 | `State` 普通字段跨线程读写，无 `volatile` | 仅状态提示 | 可见性不保证（不影响数据正确性） | 无 |

## 同步规则（加锁范围）
- 临界区：`ModbusDriverBase` 各公开方法 `await Gate.WaitAsync` 覆盖整个操作；RTU `ConnectAsync`/`DisconnectAsync` 外层 `_sync` 覆盖租约替换、内层取旧共享 `Gate`；`SerialPortManager` 的 `_ports`/`LeaseCount` 由 `_lock` 覆盖。
- 锁序：RTU `_sync` → 共享 `Gate`（单方向）；`SerialPortManager._lock` 为叶子锁（其内不取 `Gate`）→ 无环。
- 可见性：`_disposed` 用 `Interlocked`；`_lease` 归属切换在 `_sync` + 旧共享闸门下，**清空一律 `Interlocked.Exchange(ref _lease, null)` 后再 Dispose**（防止与并发 `ConnectAsync` 的 `_lease = Acquire(...)` 互相覆盖而泄漏）；`State` 为普通读写。
- 异常/释放：RTU 换租约 `catch` 内以 `Interlocked.Exchange` 清空 `_lease` 后再 Dispose、置 `Faulted`；`SerialPortLease.Dispose` 幂等；驱动同步/异步释放共用幂等位（ADR-078），且**不释放** `SemaphoreSlim`（D6）。

## 机制选择
| 候选 | 失败时序 | 结论 |
|---|---|---|
| 单闸门（实例 `Gate`；RTU 共享 `Gate`） | — | **选定**：与 ADR-074 一致，结构强制 |
| 每帧重开串口 | 慢、且多从站无法复用；时序上反而更脆 | 拒 |
| `ConcurrentDictionary` 管句柄 | 仍需帧级闸门；生命周期（引用计数关闭）更难闭合 | 拒 |

## 待确认（人定）
1. ~~**X1**~~：已定（ADR-078）——同步 `Dispose` 保持不排水（接受）；异步 `DisposeAsync` 取闸门修复。
2. **X3 / I4**：换租约「旧闸门+新句柄」窗口是**真缺陷需修**（例如让 IO 也持 `_sync`，或替换时同时持有新旧闸门），还是**记录为豁免**？
3. **检测器**：I1/I3/I5/I6 已有 Coyote 检测器；I2 部分（共享句柄/闸门前提 + 驱动级串口账目）；I4 换租约撕裂仍待抽象 `ModbusRtu`（**非目标**）。
4. ~~**X2**~~：已定（ADR-078）——同步 `DisposeCore` 不再 sync-over-async（不排水）；异步走 `DisconnectAsync`。

## 变更记录
| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿：单闸门 / RTU 串口共享租约 / 换租约窗口 / Dispose 边界 | 本次建模 |
| v2 | ADR-078 双接口释放：I6 收口幂等、X1/X2 更新为「同步不排水 + 异步优雅」、不释放闸门 | ADR-078 |
| v3 | 接入 Coyote（`SerialPortInvariants`）：I2/I3 共享句柄/闸门 + 引用计数账平、I3 异参抛、I5 租约并发释放一次；为 `SerialPortManager` 增加内部连接工厂测试缝（默认行为不变）；I2 帧内 Station 与 I4 换租约撕裂列为非目标（需抽象 `ModbusRtu`） | 本次实现 |
| v4 | 接入 Coyote（`ModbusDriverBaseInvariants`）：I1 六入口混合并发串行（无闸门/仅 Ping 绕过 负控）、I6 并发同步/异步释放幂等（无幂等位 负控）；Coyote 测试按模块分层（`Protocol/`、`Transport/`、`Support/`）；S7/Mitsubishi/OpcUa 与真实 TCP/RTU 句柄因无测试缝列为非目标 | 本次实现 |
| v5 | 接入 Coyote（`ModbusTcpDriverInvariants` / `ModbusRtuDriverInvariants`）：TCP 读串行 + X1 异步释放排水、RTU 租约账平 + X2 异步释放排水；为 `ModbusTcpDriver` 加内部客户端注入缝（默认 `new ModbusTcpNet()`，行为不变）。**发现真缺陷并修复**：RTU `DisposeCore`/`catch` 先 `_lease?.Dispose()` 再 `_lease = null`，因 `Dispose` 内取管理器 `_lock` 可被并发 `ConnectAsync` 的 `_lease = Acquire(...)` 穿插，尾随 `= null` 抹掉新租约导致**串口租约泄漏**；改为 `Interlocked.Exchange(ref _lease, null)` 原子置空后再 Dispose。TCP 建连（`ConnectServerAsync` 非 virtual）与 RTU 帧级 I2/I4/X3 仍为非目标 | 本次实现 |
