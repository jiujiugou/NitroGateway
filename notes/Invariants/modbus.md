# Modbus 驱动 并发模型

- 目标文件：`src/NitroGateway.Protocol/Modbus/`（`ModbusTcpDriver` / `ModbusRtuDriver` / `SerialPortManager` / `ModbusDriverBase`）
- 日期：2026-09-27
- 范围：单闸门串行、RTU 同端口串口共享与租约引用计数、换租约窗口、Dispose 边界。
- 非目标：`ModbusAddressParser` / `ModbusBatchPlanner` / `ModbusAddress` / `ModbusArea` / `ModbusDriverCapability`（无状态纯函数）；HslCommunication 客户端内部线程安全（外部库）；装饰器 `ReliableProtocolDriver`（见 `protocol-driver-pool.md` / ADR-077）；上层池与调用方。

## C 行为契约（这个类答应怎么用）
| 项 | 内容 |
|---|---|
| 目的 | Modbus TCP/RTU 驱动的连接/读/写/Ping；RTU 经 `ISerialPortManager` 复用同端口串口 |
| 前置 | `_connection` / `_settings` 构造后只读；`ISerialPortManager` 线程安全（Singleton）；`ReadGate` 有效 |
| 行为 | 公开读/写/Ping/Connect/Disconnect 走单闸门（`ReadGate`，ADR-074）；RTU 换租约在 `_sync` + 旧共享 `Gate` 下做「Dispose 旧 → Acquire 新」；`Dispose` 幂等 |
| 副作用 | 可能开关串口（最后一个租约释放时关闭）；改写共享 `Rtu.Station` |
| 生命周期 | TCP `_client` 每驱动一份；RTU 的 `ModbusRtu` **按端口共享**、引用计数、最后租约释放时关闭 |
| 使用期归属 | **裸借用**（池借出装饰器，ADR-077）；驱动 `Dispose` **不保证**与在途帧并发安全（见 X1） |
| 调用方义务 | 不得在 UI 线程同步 `Dispose`（X2）；不得绕过闸门直接操作底层客户端 |

## I 不变量（必须永远成立）
| # | 不变量 | 检测器（红） | 豁免/边界 |
|---|---|---|---|
| I1 | 同一驱动实例的受保护操作（Connect/Disconnect/Read/Write/Ping）对底层客户端**串行**（单闸门） | 已有 `ProtocolDriverGateConcurrencyTests`：显式 Connect 与读触发建连最大并发==1 | — |
| I2 | RTU 同端口多从站**帧级串行**：每次通信在共享 `Gate` 内先切 `Station` 再发帧 | 待写：两 RTU 驱动共享假 manager/Gate，断言帧最大并发==1 且帧内 `Station` 与发送者一致 | 依赖共享 Gate 有效 |
| I3 | 串口租约引用计数账平：同端口 N 个租约，**最后一个 Dispose 才关闭并移除**；异参 Acquire 抛异常 | 待写：`SerialPortManager` 直测（Acquire×N / Release 计数、Close 仅 1 次、异参抛） | — |
| I4 | **换租约不撕裂**：Connect 替换 `_lease` 时，不得与在途帧并发使用旧句柄 | 待写：受控交错——替换期间并发读不得进入旧句柄/旧闸门混用 | 见 X3（残余窗口待定） |
| I5 | 每个 `SerialPortLease` 恰 Release 一次（`_disposed` Interlocked） | 待写：并发 Dispose 同一 lease → Release 一次 | — |
| I6 | 驱动 `Dispose` 幂等（`_disposed` Interlocked） | 待写：并发 Dispose → 内层只关一次 | — |

## X 豁免（明知接受、写理由+风险）
| # | 豁免 | 理由 | 风险 | 假设守卫 |
|---|---|---|---|---|
| X1 | TCP `Dispose()` **不取** `ReadGate`，可与非 `DisconnectAsync` 路径的在途读并发 → 在途读撞已释放 `_client` | 池 Evict 调 `Dispose`；与 ADR-077 X1 同族（裸借用） | 在途读可能失败/异常（`DeviceReader` 已兜底为一次失败） | 表征测试（待写） |
| X2 | RTU `Dispose()` = `DisconnectAsync().GetAwaiter().GetResult()`，**同步阻塞** | `IDisposable` 同步 vs 拆除异步（S2） | UI 线程调用会卡/死锁（S1）；无 try/catch | 无（契约级，另案 S1/S2） |
| X3 | 换租约窗口内 `ReadGate` 在 IO 入口读取、`Rtu` 在用时读取，可能「旧共享闸门 + 新句柄」混用（`cs:81-82` 与基类 `ReadGate` 之间） | 依赖 `_sync` + 旧闸门约定（`cs:77-90`） | 潜在帧交错（低概率，随重连触发） | 待评估（是否纳入 I4 检测器） |
| X4 | `State` 普通字段跨线程读写，无 `volatile` | 仅状态提示 | 可见性不保证（不影响数据正确性） | 无 |

## 同步规则（加锁范围）
- 临界区：`ModbusDriverBase` 各公开方法 `await ReadGate.WaitAsync` 覆盖整个操作；RTU `ConnectAsync`/`DisconnectAsync` 外层 `_sync` 覆盖租约替换、内层取旧共享 `Gate`；`SerialPortManager` 的 `_ports`/`LeaseCount` 由 `_lock` 覆盖。
- 锁序：RTU `_sync` → 共享 `Gate`（单方向）；`SerialPortManager._lock` 为叶子锁（其内不取 `Gate`）→ 无环。
- 可见性：`_disposed` 用 `Interlocked`；`State`/`_lease` 为普通读写。
- 异常/释放：RTU 换租约 `catch` 内 Dispose 新租约、置 `Faulted`（`cs:93-99`）；`SerialPortLease.Dispose` 幂等。

## 机制选择
| 候选 | 失败时序 | 结论 |
|---|---|---|
| 单闸门（实例 `ReadGate`；RTU 共享 `Gate`） | — | **选定**：与 ADR-074 一致，结构强制 |
| 每帧重开串口 | 慢、且多从站无法复用；时序上反而更脆 | 拒 |
| `ConcurrentDictionary` 管句柄 | 仍需帧级闸门；生命周期（引用计数关闭）更难闭合 | 拒 |

## 待确认（人定）
1. **X1**：TCP `Dispose` 不取闸门（在途读可能撞已释放客户端）——接受（对齐 ADR-077）／ 修（`Dispose` 也走闸门）？
2. **X3 / I4**：换租约「旧闸门+新句柄」窗口是**真缺陷需修**（例如让 IO 也持 `_sync`，或替换时同时持有新旧闸门），还是**记录为豁免**？
3. **检测器**：I2–I5 目前**无**，是否接受「实现阶段先写红再绿」的顺序？（I1 已有）
4. **X2**：RTU `Dispose` 同步阻塞确认归入另案 **S1/S2**（`notes/已知约束.md`）？

## 变更记录
| 版本 | 变更 | 来源 |
|---|---|---|
| v1 | 初稿：单闸门 / RTU 串口共享租约 / 换租约窗口 / Dispose 边界 | 本次建模 |
