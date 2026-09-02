# ADR-072: OPC-UA 会话自愈封装（层 3 · P1-2）

- 日期: 2026-09-02 | 状态: 决策已定（已实现并验收，见 AC-opcua-layer3-selfhealing）
- 来源: docs/07-OPC-UA四层生产化封装审查与实施计划.md 层3 P1-2（工业可靠性）与 §5 W3 约束卡
  （五因子 3×3×4×3×3=324 → XL，先降险再动工）；SDK 1.5.378.156 包内 XML 文档与 OPC Foundation
  官方源码/Docs 复核；ADR-019（驱动闸门 `_gate`、不产伪值）、ADR-070（层1 Browse 封装先例）、
  ADR-071（订阅采集，层2）

## Context

docs/07 层3 审查结论：层3（工业可靠性）"能恢复但粗粒度"——已连接后的断线目前由
`ReliableProtocolDriver` 整轮重建 Session（能连回来，但丢订阅、开销大、监控项丢失），
`OpcUaDriver` 未接入 `Session.KeepAlive`、未使用 `SessionReconnectHandler`，订阅迁移
（Transfer/Recreate）也未封装。W3 约束卡评分 324 → XL，要求拆子步骤（KeepAlive 检测 →
会话保活重连 → 订阅迁移），每步独立验证；重连状态机必须对齐现有 `DriverState` 与
`DeviceHealthMonitor`（Online/Offline 单一权威来源，别造第二套）；初始建连重试仍走
`ReliableProtocolDriver`，自愈只接管"已连接后的断线"；`TransferSubscriptionsAsync` 失败降级
`RecreateSubscriptionsAsync`，并补断网恢复测试。

对 SDK 能力的事实复核（决策依据）：

- `Session.KeepAlive` 事件存在；`KeepAliveEventArgs` 提供 `Status/CurrentState/CurrentTime/
  CancelKeepAlive`；`Session.KeepAliveStopped` 指示会话保活是否已停（服务端判定会话超时）。
- `SessionReconnectHandler.BeginReconnect(ISession, int, EventHandler)` 的第二个 int 参数在
  SDK 1.5.378.156 中实为 **`reconnectPeriod`（毫秒）**，不是 docs/07 表格所写的 `maxRetries`：
  内部 `Timer(OnReconnect, ..., reconnectPeriod, Timeout.Infinite)` 周期性尝试重连，会话仍在
  生命周期窗口内（`LastKeepAliveTime + SessionTimeout > now`）会持续按周期重试，并非有界重试次数。
  docs/07 该处参数命名有误，本 ADR 以 SDK 源码为准予以更正，避免实现者误解。
- `SessionReconnectHandler.DoReconnect()` 的语义：先尝试 `m_session.Reconnect()`（**原地重连，
  保 Session 实例**，服务端保留订阅）；仅在会话真正过期后才 `Session.Recreate(m_session)`
  （**重建新 Session**）。重连成功（任一路径）回调一次 `callback(handler, null)`，可经
  `handler.Session` 引用是否变化区分"原地保住 vs 重建"。
- `Session.Recreate(template)`（SDK 源码核实）内部：建新会话 → `TransferSubscriptions(...)`
  → 失败则逐订阅 `subscription.Create()` 全量重建。即 **Transfer→Recreate 降级由 SDK 内置**，
  官方 `Docs/TransferSubscription.md` 亦明确："Existing clients which use the
  `SessionReconnectHandler` get the improved support (no client changes necessary)。"
  因此本项目**不得**手写第二套订阅迁移逻辑（会与 SDK 内置迁移形成双 Transfer / 状态分裂）。
- `SessionReconnectHandler` 另提供 `Session`/`State` 属性、`CancelReconnect()`、`Dispose()`，
  用于生命周期清理与防重入。

## Decision

- D1 **KeepAlive 接入与事件分类（P1-2a）**：`ConnectAsync` 成功后（`_gate` 内）绑定
  `_session.KeepAlive += OnSessionKeepAlive`；`DisconnectAsync`/`Dispose` 解绑。事件分类只决定
  **是否启动自愈与如何记日志/上报**，不手写两套恢复路径：
  - `e.Status` 为 Good：会话存活，无动作（可刷新"最后保活时间"）；
  - `e.Status` 为 Bad 且无进行中的重连：判定为通信中断（网络闪断或会话过期两类都由此进入）→
    启动 `SessionReconnectHandler`。会话是否"真正过期"由 SDK 内部依据
    `LastKeepAliveTime + SessionTimeout` 与 `KeepAliveStopped` 判定：窗口内持续原地重连，
    过期才重建——这正好对应 docs/07 的"闪断保 Session 实例，真正过期才重建"。
- D2 **自愈边界（P1-2b）**：自愈只接管"已连接后的断线"。初始建连、以及 `DriverState`
  处于 `Disconnected/Faulted` 后的（重）建连，仍全部走 `ReliableProtocolDriver`（Polly 自动建连
  + 指数退避），二者互不抢占。重连窗口内驱动 `State` **保持 `Connected`**（详见 D5），
  从而避免上层 `ReliableProtocolDriver.ReadBatchAsync` 的自动建连（整轮重建 Session）与
  KeepAlive 驱动的会话保活重连在同一断点上"双车抢道"。
- D3 **复用 `SessionReconnectHandler`（P1-2b 落地）**：闪断时
  `BeginReconnect(session, reconnectPeriodMs, callback)`；`reconnectPeriodMs` 取可配置重连周期
  （默认建议 1s，SDK `DefaultReconnectPeriod`），**它是重连周期而非重试次数**（更正 docs/07）。
  回调内：
  - `ReferenceEquals(handler.Session, 断线前会话)` → 原地重连成功，Session 实例未换、订阅仍在，
    驱动维持 `Connected`，仅记日志；
  - 否则 → 会话已重建，替换 `_session` 引用、重绑 KeepAlive、核对订阅激活状态后进入 D4 恢复核验。
  同一时刻只允许一个活动 handler（`BeginReconnect` 在已有重连时抛 `BadInvalidState`，用
  `handler.State` 做防重入）。
- D4 **订阅恢复（P1-2c）**：**首选复用 SDK 内置迁移**——`Session.Recreate` 内部已实现
  "先 `TransferSubscriptions`、失败降级逐订阅 `Create()`"（源码核实），随 `SessionReconnectHandler`
  重建路径自动生效，本项目不重复实现。自愈层只做**可观测核验**：重建完成后检查订阅是否仍激活
  （`IsSubscriptionActive` / 是否仍在产生通知）；若 SDK 内置迁移未恢复到可用（如服务端不支持
  Transfer 且重建后订阅对象脱离），再走既有的 `_gate` 串行订阅路径重拉（`EnsureSubscriptionAsync`
  幂等重建，属 ADR-071 既有能力），或在 `_gate` 内显式 `RecreateSubscriptionsAsync`。**禁止**
  在 SDK 已迁移成功后再次手动 Transfer/Recreate（双 Transfer 会造成订阅 id 漂移）。
- D5 **状态与健康对齐（联动点）**：
  - `DriverState`：重连窗口（会话未过期、原地重连进行中）保持 `Connected`；原地重连成功维持
    `Connected`；会话重建成功回到 `Connected`；自愈最终失败（重建抛出/周期中止/用户 Disconnect）
    才置 `Faulted`/`Disconnected`，把后续交给 `ReliableProtocolDriver` 重试管线。
  - **不产生第二套 Online/Offline 判定**：驱动/自愈代码只维护自身 `DriverState` 与健康上报
    事件，绝不直接改设备 Online/Offline；`DeviceHealthMonitor` 仍是唯一 SST（`IDeviceHealthMonitor`
    文档明示），`CircuitBreakerHealthListener`（Online→Reset / Offline→Evict+Trip）对外行为不变。
  - 自愈价值体现在"缩短失败窗口"：闪断在健康阈值（连续 3 次失败）内自愈 → 采集连续成功，
    不触发离线；超过阈值仍按既有模型离线并 Evict，这是既有契约，自愈不改它。
- D6 **生命周期与并发纪律**：`Disconnect`/`Dispose` 必须先 `CancelReconnect()` + `Dispose()`
  停掉重连 handler、解绑 `KeepAlive`，再关会话（顺序不可反，防止重连回调访问已关闭会话）。
  KeepAlive/ReconnectComplete 回调运行在 SDK 定时器/线程池线程：回调内不得做长操作、不得在回调内
  长时间持 `_gate`（避免与 `Disconnect` 持 `_gate` 时互相等待）；对 `_session` 引用的替换/状态迁移
  需在 `_gate` 内以有界等待完成或经互斥保护，事件回调幂等（会话置空后回调直接返回）。
- D7 **恢复失败兜底 = 既有轮询（不加新机制）**：自愈最终失败、`State` 回到
  `Faulted/Disconnected` 后，采集回到 `ReliableProtocolDriver` 自动建连 + 轮询（v1 路径，ADR-071
  D2 的"订阅失效回退轮询"同一机制）与 `SubscriptionCoordinator` 周期性激活，不新增状态机。

## Alternatives

- A. 保持现状（整轮重建 Session）：实现量最小，但丢订阅、丢监控项、断线窗口长、每次断线都重握手，
  与层3 工业可靠性目标冲突，否决。
- B. 手写完整重连/订阅迁移状态机（自己调 `ReconnectAsync`/`TransferSubscriptionsAsync`/
  `RecreateSubscriptionsAsync`）：表面"完全可控"，但与 SDK 内置 `SessionReconnectHandler`/
  `Session.Recreate` 的迁移逻辑重复，易出双 Transfer、边界难测（SDK 官方明确"no client changes
  necessary"），违背仓库"已有功能绝不自己重写"硬约束，否决。
- C.（选定）复用 `SessionReconnectHandler` + `Session.Recreate`（内置 Transfer→Recreate），
  自愈层只做 KeepAlive 触发、回调状态对齐、订阅激活核验、生命周期清理与日志/健康上报：
  改动集中在 `OpcUaDriver` 一处，与 SDK 演进同步，最小且可独立验证。

## Rationale

- 复用优先（docs/07 §3）：会话保活/重连/订阅迁移是 SDK 已实现能力，"你要写的只有业务映射、
  生命周期管理与对接"——自愈封装写的是 KeepAlive 接入、状态对齐、清理与核验，不是协议。
- 单一恢复路径：只保留 `SessionReconnectHandler` 一条恢复路径 + `ReliableProtocolDriver`
  一条建连路径，二者按 `DriverState` 边界严格分工（已连接→自愈；未连接→Reliable），避免
  "双车抢道"导致的会话竞态与订阅漂移。
- 单一权威来源：Online/Offline 只能由 `DeviceHealthMonitor` 判定；驱动只报 `DriverState`。
  若在自愈代码里再判一次在线，会与健康监控/熔断产生第二套真相，违背 W3 约束与现有 SST 契约。
- 事实更正入档：docs/07 把 `BeginReconnect` 第二参数写作 `maxRetries`，与 SDK 源码不符
  （实为 `reconnectPeriod` ms）；ADR 记录正确语义，避免实现者按"重试次数"理解而误配。
- `_gate` 串行 + 回调不阻塞：Session 非线程安全（ADR-019），订阅配置/读写/断连必须同闸门；
  回调线程不得持闸门，防止死锁。

## Consequences

- 网络闪断（会话未过期）不再整轮重建：Session 与订阅原地保住，重连开销与丢通知窗口显著下降；
  断线后"拔网线→恢复→订阅续采"成为可验证行为（W3 要求的断网恢复测试）。
- 会话真正过期仍由 SDK 重建并自动 Transfer→Recreate，上层无感；不支持 Transfer 的服务端
  自动走全量重建（订阅恢复，丢失期间样本与轮询语义一致，不承诺零丢失）。
- 采集侧失败的"失败窗口"缩短 → `DeviceHealthMonitor` 对瞬断误判离线的概率下降；熔断/Evict
  对外行为不变（这是契约，不是可优化点）。
- `OpcUaDriver` 增加 KeepAlive 绑定、重连 handler 生命周期与状态对齐逻辑（局限于单文件）；
  需补：KeepAlive 分类/防重入单测、断网恢复集成测试、状态对齐与生命周期清理单测。
- 层次 4（安全/凭据）、Browse/订阅的既有行为、Modbus/S7 路径不受影响。

## 载荷墙（硬约束）

- 不得改 `IProtocolDriver` / `ISubscriptionSource` 公共接口（只增不改）。
- 不得改 `DeviceHealthMonitor` / `CircuitBreaker` / `IDeviceHealthListener` 对外行为；
  Online/Offline 判定仍由 `DeviceHealthMonitor` 唯一负责，禁止造第二套在线判定。
- 初始建连与已断开后的（重）建连仍走 `ReliableProtocolDriver`；自愈只接管"已连接后的断线"，
  二者不得在同一断点并发抢道。
- 订阅迁移复用 SDK `SessionReconnectHandler` / `Session.Recreate` 内置的
  Transfer→Recreate 降级；禁止手写第二套迁移逻辑造成双 Transfer。
- 不得改 Modbus/S7 采集路径；订阅数据仍走唯一 `IPointValuePipeline`/`IDataDispatcher`
  （ADR-071），不新增第二套数据路径。
- KeepAlive/重连回调不得阻塞、不得在回调内长时间持 `_gate`；`Disconnect`/`Dispose` 必须
  先停重连 handler 再关会话，并解绑事件。
- 不把 OPC UA 做成对外 Server（docs/07 明确不做，北向仍 MQTT）。

## 变更记录

- 2026-09-02 创建，决策定稿（复用 SDK 会话自愈 + 状态/健康单一权威 + 生命周期纪律）。
  SDK 事实已复核：`BeginReconnect` 第二参数为 `reconnectPeriod`(ms)（更正 docs/07 的 maxRetries
  表述）；`Session.Recreate` 内置 Transfer→Recreate 降级；官方 TransferSubscription 文档确认
  `SessionReconnectHandler` 使用者无需客户端改动即可获得订阅迁移支持。
- 2026-09-02 实现落地：`OpcUaDriver.cs` 接入 KeepAlive（D1）、复用 `SessionReconnectHandler`
  会话保活重连（D3，D2 自愈边界 + D5 状态对齐 + D6 生命周期纪律），订阅恢复仅做可观测核验
  并复用 SDK 内置 Transfer→Recreate（D4/D7 回退既有轮询）；单元/集成用例 + AC 验收 PASS
  （细节见 AC-opcua-layer3-selfhealing 实测回填）。
