# ProtocolDriverPool 并发模型

- 目标文件：`src/NitroGateway.Protocol/Abstraction/ProtocolDriverPool.cs`
- 日期：2026-09-26
- 范围：`_drivers` 字典一致性、驱动生命周期账（恰释放一次）、`Dispose` 幂等、借出边界。
- 非目标：驱动实例内部并发（`ReliableProtocolDriver` 闸门）、`_factory` 自身线程安全、连接重建调度、调用方对借出的使用期安全（见 X1）。

## C 行为契约（这个类答应怎么用）
| 项 | 内容 |
|---|---|
| 目的 | 按 `device.Id` 复用长连接；连接指纹变化则重建并释放旧驱动 |
| 前置 | 池未销毁；`device` 非 null；`_factory` 线程安全；调用期间 `device.Connection` 快照不被并发改写 |
| 行为 | `GetOrCreate` 命中同 key→同实例；key 变/未命中→Create+安装，旧实例锁外释放。`Evict` 移除并释放，已销毁→no-op。`Dispose` 幂等清空全释放 |
| 副作用 | 可能创建/释放驱动（释放含 socket 关闭）；返回**裸借用**引用 |
| 生命周期 | DI 单例，进程级；宿主关停由容器调 `Dispose` |
| 使用期归属 | **裸借用**；释放后到达的调用由装饰器快速失败（I7，ADR-077），在途调用可能与释放并发而失败（X1） |
| 调用方义务 | 不得自行 `Dispose` 借出驱动；不得假设使用期间其存活 |

## I 不变量（必须永远成立）
| # | 不变量 | 检测器（红） | 豁免/边界 |
|---|---|---|---|
| I1 | 同设备同 key 并发 `GetOrCreate` 只建一次且返回同一实例 | T1：8 并发→`Created==1` 且全 `Same`（已有 `ProtocolDriverPoolConcurrencyTests:30`）；负控=永久 miss（**待自动化**） | 不同设备间不互斥（设计） |
| I2 | 每设备至多一条目；条目只经 Evict/Dispose/换键移除，移除必配一次释放 | T2：混合并发（换键+Evict+Dispose）后 `Created==Σ释放`（**待写**） | — |
| I3 | 每个创建驱动**恰释放一次** | T3：并发展与 Dispose→每驱动 `DisposeCount∈{0,1}`（已有 `:51`）；负控=删 `_disposed` 双检（**待自动化**） | 调用方自行 Dispose 会破账（违约） |
| I4 | `Dispose` 幂等；销毁后 `GetOrCreate` 抛 ODE、`Evict` no-op，不再安装 | T4：并发 `Dispose`×N 每驱动 `DisposeCount==1`（**待写**） | — |
| I5 | `Create` 抛异常不泄漏、无半装条目；换键失败保留旧条目 | T5：`Create` 抛异常后账平且旧条目仍可复用（**待写**） | 工厂抛异常时无实例可释放 |
| I6 | 释放（`stale.Dispose`/`Dispose` 循环）不在临界区内 | T6：阻塞式 `Dispose` 时另一设备 `GetOrCreate` 仍完成（**待写**） | 仅覆盖池锁，不覆盖驱动闸门 |
| I7 | 释放后到达的调用**快速失败**（返回失败、不触达内层、不抛异常）；`Dispose` **不排水**（ADR-077） | G2：释放后 `ReadBatch/Write/Connect` 返回失败不抛；R1：在途读未完成时 `Dispose` 立即返回；负控=`NoGuardWrapper`/`DrainWrapper`（**已实现** `ReliableProtocolDriverConcurrencyTests`） | 在途调用与释放并发时允许失败（X1）；**不承诺**等排空 |

## X 豁免（明知接受、写理由+风险）
| # | 豁免 | 理由 | 风险 | 假设守卫（前提变了就复审） |
|---|---|---|---|---|
| X1 | 借出驱动可在调用方使用期被释放（Evict / Dispose / 并发换键）；此刻在途调用可能失败 | 无租约（成本高/易漏放）；曾试装饰器排水消除该窗口，但**在带 SynchronizationContext 的桌面宿主会与在途续体互等 → UI 死锁**，故回退（ADR-077） | 在途调用可能失败（一次、可恢复）；不再崩溃/500 | R1 钉死"不排水"防回退；调用方兜底见 `WriteService`/`OpcUaBrowseController` try/catch |
| X2 | `GetOrCreate` 无 CT；`Create`（含 Polly 构造）在锁内 | 4 个驱动构造器均纯赋值不建连（已核 `OpcUaDriver:54`/`ModbusTcpDriver:24`/`ModbusRtuDriver:28`/`S7Driver:28`） | 未来构造器若做 I/O 会阻塞全池 | 守卫：断言 `Create` 不阻塞/不建连（**缺**） |
| X3 | `BuildKey` 读可变 `device.Connection` 快照 | 无深拷贝；缓存整体替换不原地改 | 若有人原地改会读到撕裂 key | 守卫：断言快照不可变 / `BuildKey` 对同快照确定（**缺**） |

## 同步规则（加锁范围）
- 临界区：`lock(_gate)` 覆盖字典查/建/装/移除/清空 + `_disposed` 双检；**`Create` 在锁内**（`cs:51`）；**释放移锁外**（`cs:55,71,90-94`）。
- 锁序：全局单锁，无嵌套 → 无死锁环。
- 可见性：`Volatile.Read(ref _disposed)` + `Interlocked.Exchange` 写。
- 异常/释放：`Create` 抛异常由 `lock` 自动释放；`stale.Dispose()` 锁外**无 try/catch**，异常向调用方传播（见待确认 1）；`Dispose` 循环吞异常（`cs:93`，与之不一致）。

## 机制选择
| 候选 | 失败时序 | 结论 |
|---|---|---|
| A. 单锁 + 锁内 Create（现状） | `Create` 若变慢 I/O 会阻塞同池其他设备；当前纯构造→不触发 | **选定** |
| B. 单锁双检 + Create 移锁外 | 无占位→两路并发错过缓存**重复创建**（破 I1） | 拒 |
| C. `ConcurrentDictionary.GetOrAdd` + 每 key Lazy | 工厂可能被多次调用（破 I1）；Evict/Dispose 生命周期难闭合 | 拒 |

## 待确认（人定）
1. **`stale.Dispose()` 异常策略**：传播（现状）/ 吞+日志 / 吞静默。〔倾向**吞+日志**；`Evict` 常从健康回调线程调用，传播会打断健康通知链。目前 `WriteService`/`OpcUaBrowseController` 已加调用方兜底，但 `Evict` 路径仍未处理〕

> 已定：**X1 裸借用接受**——无租约；**不做排水**（ADR-077）；以「装饰器释放后快速失败 + 调用方兜底」把后果降为「一次干净失败」。
> 记账项（默认接受现状）：X2/X3 维持现状、机制 A、快照不可变走调用方契约。

## 变更记录
| 版本 | 变更 | 来源 |
|---|---|---|
| v1–v4 | 早期全流程建模（含拍板/Gate 阶段） | 历史 |
| v5 | 按轻量模板重排为三段主体；检测器待写项标 T2–T7；X 增加假设守卫 G1/G2 | 流程瘦身 |
| v6 | 方案一实现：装饰器加释放排水（I7）、X1 由「崩溃」降为「干净失败」；G1/G2/负控已落地并绿 | 本次实现 |
| v7 | **回退排水（ADR-077）**：I7 改为「释放后快速失败、不排水」；X1 保留豁免并记录回退原因（桌面 UI 死锁）；检测器改为 G2/R1 + 双负控 | 方案 B |
