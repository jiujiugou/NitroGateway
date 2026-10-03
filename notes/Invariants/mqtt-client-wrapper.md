# MqttClientWrapper 并发模型（Coyote 版）

- 目标文件：`src/NitroGateway.Transport/MQTT/MqttClientWrapper.cs`（+ `MqttHostedService.cs`）
- 日期：2026-10-02
- 范围：连接状态机 / 重连单实例 / `_reconnectCts` 生命周期 / 释放 / 开关语义 / 订阅重放 / 通知重入。
- 非目标：MQTTnet、Polly 内部（不重写，不进调度器）；`_channel` 满时的容量行为（另案）；消息下行。

> **本文件的单位是"可被 Coyote 调度器触发的断言"，不是概念。** 每条必须回答五问：断言什么、谁们交错、在哪切换、接缝在哪、怎么证明能红。

## 0. Coyote 运行前提（决定能不能跑）
| 项 | 内容 |
|---|---|
| 被 rewrite | `NitroGateway.*`（含 `NitroGateway.Transport.MQTT` 及其依赖） |
| **不可控外部** | `MQTTnet`（连接/回调实现）、`Polly.Core`（退避定时）→ **不重写、不进调度**，必须挡在接缝后；`MaxReconnectAttempts` 夹到 0–2，退避基数夹到最小 |
| 入口 | `tests/NitroGateway.CoyoteTests/Program.cs` 注册；迭代 50–100 |
| **待补接缝** | ① `NitroGateway.Transport.MQTT.csproj` 加 `InternalsVisibleTo NitroGateway.CoyoteTests`；② `CoyoteTests.csproj` 引用 MQTT 项目；③ 可控 `MQTTnet.IMqttClient` 替身（见 §3） |
| 已知 | `MqttClientWrapper` 为 `sealed` → 无子类坏实现；负控需内部接缝或记豁免（见 §5） |

## 1. 共享资源与到达流（起点）
| 资源 | 保护 | 谁碰 | check-then-act / 缺口 |
|---|---|---|---|
| `_state` | `_stateLock` | 所有公开方法、重连循环、toggle 回火 | `ConnectAsync` 读 Connected → 写 Connecting（L109→L112，TOCTOU） |
| `_subscriptions` | `_subscriptionLock` | `SubscribeAsync` 写 L306；`ReplaySubscriptionsAsync` 快照 L609 | 无（锁内） |
| `_reconnectLoopActive` | `_reconnectLock` | `StartReconnectLoop` L582；`finally` L574 | 读-改-写在锁内 ✅ |
| `_reconnectCts` | **无** | 建/清 L541/L572；`CancelReconnect` L620 | **无同步** → 与取消/释放竞态 |
| `_inner`（MQTTnet，非线程安全） | 无（靠状态前置判断） | Connect/Disconnect/Publish/Subscribe/Dispose 多流 | 并发 `ConnectAsync`（调用方 + 监督循环 + 重连） |
| `_toggle.EnabledChanged` | event | `SetEnabledAsync` 触发 → `OnEnabledChanged` fire-and-forget | 快速 toggle 与重连交错 |

**到达流（≥2 才值得上 Coyote）**
1. 调用线程：`Connect/Disconnect/Publish/Subscribe/DisposeAsync`
2. MQTTnet 回调线程：`OnDisconnectedAsync`（模拟断线）、`OnMessageReceivedAsync`
3. 重连循环（`_ = TryReconnectAsync`）
4. toggle 事件（`_ = ApplyEnabledAsync` / `ApplyDisabledAsync`）
5. 状态监听者（`_ = NotifyListenerAsync`）
6. `MqttHostedService` 监督循环（周期 `ConnectAsync`）
7. Polly 重试（**不重写**，不进调度）

## 2. 不变量（Coyote 专版）
> 每条：**断言**（检测器真正 assert 的谓词）·**参与者**·**交错点/门**·**接缝**·**负控**·**够不着**。

### I1 状态写入串行、读不撕裂
- 断言：任意交错后 `State` ∈ 合法枚举；无"丢写"（写入序列的最终值 == 某次写入）；getter 不返回半更新值。
- 参与者：①公开方法 ②重连循环 ③toggle 回火（≥2 路改状态）。
- 交错点：`SetState` 的 `lock` 前后（L350–361）。
- 接缝：可控 inner（让 Connect/Disconnect 在门处阻塞，制造多路同时改状态）。
- 负控：`_state` 无 `_stateLock` 的坏实现（读-改-写丢写）。
- 够不着：MQTTnet 自身状态；`NitroMetrics`。

### I2 重连单实例（核心）
- 断言：任意时刻"正在跑的重连循环"数 ≤ 1；`_reconnectLoopActive` 在成功/失败/取消退出后必为 false。
- 参与者：③首连失败触发、③`OnDisconnectedAsync` 触发、⑥监督循环、④toggle enable——多路竞争 `StartReconnectLoop`。
- 交错点：`lock (_reconnectLock)` 内判空与置位之间（L584–588）；`finally` 复位（L574）。
- 接缝：可控 inner 的 `ConnectAsync` 阻塞于门 + 计数（区分普通连接与重连尝试）。
- 负控：去掉 `_reconnectLock` 守卫的坏实现 → 多循环并发。
- 够不着：Polly 在单循环内的重试次数（不重写）→ 用 `MaxReconnectAttempts` 夹小。

### I3 `_reconnectCts` 生命周期（已确认真缺陷，2026-10-03 修复）
- 断言：至多一个活跃 CTS；`CancelReconnect`/`DisposeAsync` 后 field 为 null 且只 Dispose 一次；不抛 `ObjectDisposedException`；不被旧循环的 `finally` 误清/误 Dispose 新 CTS。
- 参与者：③重连循环建/清（L541/L572）vs 取消方 `CancelReconnect`（来自 `Disconnect` L203 / `ApplyDisabled` L374 / `DisposeAsync` L323）。
- 交错点：`_reconnectCts = new`（L541）与 `_reconnectCts?.Dispose(); = null`（L620–624）之间；`finally`（L572）。
- 接缝：可控 inner + **CTS 工厂/观测**（包一层统计 Dispose 次数；或在建模阶段决定加内部接缝）。
- 负控：**修复前实现即种子**——`notes/Invariants` 记录：修复前 `ObservingCts.DisposeCount` 交错下取 2（6 连跑 5 红，`实际 2`）；修复后 8 连跑全绿。
- **根因**：`_reconnectCts` 无同步，循环 `finally`（`?.Dispose(); = null`）与外部 `CancelReconnect`（`?.Cancel(); ?.Dispose(); = null`）是**两条独立 read-then-act 路径**，同时读到同一实例 → 重复 Dispose / 对已释放实例 `Cancel()` 抛 ODE / 取消后漏 Dispose 三态摇摆；`_reconnectGuard` 只锁循环单实例，管不到外部取消。
- **修复**：统一为**原子取走**——`TakeReconnectCts()` 用 `Interlocked.Exchange(ref _reconnectCts, null)`；`CancelReconnect` 取到后 Cancel+Dispose 各一次；循环 `finally` 只取走并 Dispose（正常退出不 Cancel）；`DisposeAsync` 防御性取走残余。循环内先取 `token` 再暴露字段，避免外部释放后访问 `.Token()` 抛 ODE。
- 够不着：Polly 取消传播的精确时序。

### I4 释放后不再连接/重连；管道关闭
- 断言：`DisposeAsync` 完成后，后续 `ConnectAsync` 不触达 inner（`ConnectCalls` 不增）；`_channel.Writer` 已完成（`TryWrite` 失败）；`_toggle.EnabledChanged` 已退订。
- 参与者：`DisposeAsync`（L319）vs 在途重连循环 / toggle enable / 监督循环。
- 交错点：`SetState(Disconnected)`（L325）与在途 `TryReconnectAsync` 的 `SetState(Connected)`（L527）之间。
- 接缝：可控 inner（Dispose/Disconnect 阻塞）+ ToggleFake + 可观测 channel。
- 负控：Dispose 不 `CancelReconnect` / 不退订 toggle 的坏实现。

### I5 开关语义（Disabled 不连、Enable 恢复、禁用期断线不重连）
- 断言：`Disabled` 下不发起连接；禁用期间 `SimulateDrop` 不增 `ConnectCalls`；`Enable` 后可恢复 `Connected`。
- 参与者：④toggle 回火 vs ③重连循环 vs ①`ConnectAsync`。
- 交错点：`OnEnabledChanged` 的 fire-and-forget（L364–370）与在途 `ConnectCoreAsync`。
- 接缝：ToggleFake（可快速 toggle）+ 可控 inner。
- 负控：忽略 toggle 的坏实现（照连/照重连）。

### I6 订阅重放与 `_subscriptions` 一致
- 断言：重连成功后 `inner.SubscribedTopics` 覆盖所有已记录订阅；并发 `SubscribeAsync` 与 `ReplaySubscriptionsAsync` 不丢订阅、不抛（集合不损坏）。
- 参与者：①`SubscribeAsync` 写 dict（L306）vs ③重连循环快照（L609）+ `ReplaySubscriptionsAsync` 订阅（L611–616）。
- 交错点：快照与重放之间新订阅写入。
- 接缝：可控 inner 记录订阅顺序。
- 负控：无 `_subscriptionLock` 的坏实现 → 并发写 `Dictionary` 损坏/抛异常。

### I7 状态通知不在锁内（防重入死锁）
- 断言：监听者回调中反调 `State`（或 `ConnectAsync`）不死锁。
- 参与者：`SetState` 的通知（L360–361）vs 监听者回调。
- 交错点：`lock (_stateLock)` 与 `StateChanged?.Invoke`。
- 接缝：ListenerFake（回调内读 `State`）。
- 负控：在锁内 `Invoke` 的坏实现 → 死锁（需 `WithPotentialDeadlocksReportedAsBugs(true)`）。

## 3. 测试夹具契约（Fake 必须"会 yield"）
| Fake | 角色 | **可阻塞/发信号的方法** | 记录 |
|---|---|---|---|
| `ControllableMqttInner`（新建） | `MQTTnet.IMqttClient` | `ConnectAsync`/`DisconnectAsync`/`SubscribeAsync` 各配一个 `TaskCompletionSource` 门与"进入/放行"信号；**不得**用 `Task.FromResult` 返回连接结果 | ConnectCalls、SubscribedTopics、每方法并发计数 |
| `ToggleFake` | 触发 `EnabledChanged` | `SetEnabledAsync` 可立即/可控触发 | 事件次数 |
| `ListenerFake` | 回调内反调 `State` | `OnStateChangedAsync` | 状态序列 |

> **假绿警戒**：Fake 里即时完成的 `Task.FromResult` **没有可抢占点**，Coyote 无处交错 → 正例永远通过。连接/断开/订阅路径必须真正 await 一个门。

## 4. Coyote 陷阱对照（逐条勾）
- [ ] Fake 用 `Task.FromResult` → 无可抢占点，必须换门控 await
- [ ] `Thread.Sleep`/墙钟 → 换 `Task.Delay`/信号
- [ ] `System.Threading.Lock` 周期死锁误报 → 按用例关（I7 例外，需开）
- [ ] `_ = Task.Run(...)` fire-and-forget → 必须有"静默信号"等它，否则测试早退、竞态没发生
- [ ] Polly 退避不重写 → 别指望控它，夹小或不进用例

## 5. 阻塞项（人定）
1. **负控策略**：`MqttClientWrapper` 为 `sealed` → 抽一个内部小接缝（如可注入 `IReconnectCoordinator`/CTS 工厂）做真负控，还是按 `SerialPortLease` 先例记"无负控豁免"？
2. ~~**I3 定性**：`_reconnectCts` 无同步是现状。Coyote 若抓到反例 → 修（回实现）还是显式豁免？~~ **已定（2026-10-03）：确认缺陷，回实现修复**（原子取走），正例检测器由红转绿。
3. **`MqttHostedService`**：监督循环作为第 6 条到达流是否纳入本模型，还是列为非目标（跨服务、`Task.Delay` 半可控）？

## 6. 变更记录
| 日期 | 变更 | 来源 |
|---|---|---|
| 2026-10-03 | I3 `_reconnectCts` 竞态确认并修复（原子取走）；`[Mqtt] I3` 检测器红→绿；全套 63 例通过 | 本会话（修复 MQTT CTS 竞态） |
