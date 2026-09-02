# AC — OPC-UA 层次 3 会话自愈（P1-2）

> 状态：**已实现并验收（ADR-072 层3 会话自愈，2026-09-02）**。行为与源码检查证据已回填，
> 逐条 PASS/FAIL 见文末"实测回填"。已知约束（沿用层2 结论）：进程内测试服务器
> `CustomNodeManager2` 不产生 DataChange 通知，凡依赖"订阅推送续采"的通知断言属测试服务器
> 模拟限制（已在干净 HEAD 上复现同样的 3 个失败，见 worklog），非驱动缺陷；本次层3 行为用例
> 通过 KeepAlive 触发 + 读恢复（不依赖 DataChange）验证。

## 范围

为已连接的 OPC-UA 会话增加 KeepAlive 触发的会话级自愈：网络闪断（会话未过期）经
`SessionReconnectHandler` 原地重连保 Session 实例；会话真正过期由 SDK 重建并自动
Transfer→Recreate 恢复订阅；重连状态与 `DriverState` 对齐、不产生第二套 Online/Offline 判定；
Disconnect/Dispose 清理重连生命周期；自愈最终失败回退既有轮询兜底。

## ADR 引用

ADR-072（层3 会话自愈，本验收范围）、ADR-071（订阅/轮询共用管道 + `ISubscriptionSource`）、
ADR-019（驱动 `_gate` 串行、不产伪值）、ADR-070（层1 Browse）、ADR-062/053（订阅语义对齐项）。

## 不在本次范围

层次 1 Browse（ADR-070）、层次 2 订阅采集（ADR-071）、层次 4 安全与证书；`IProtocolDriver` /
`ISubscriptionSource` 公共接口；`DeviceHealthMonitor` / `CircuitBreaker` /
`IDeviceHealthListener` 对外行为；Modbus/S7 路径；`RawPointValue` 字段扩展；不把 OPC UA
做成对外 Server。

## AC

- AC-1：KeepAlive 检测接入（P1-2a）——连接成功后绑定 `Session.KeepAlive`，断开时解绑；
  按事件状态分类触发（Good 无动作 / Bad 启动自愈），同一会话不并发启动多个重连。
  - 行为：运行 `OpcUaDriverKeepAliveTests`（需新增）：模拟 KeepAlive Good 不启动重连；
    模拟 Bad（通信中断）启动一次 `SessionReconnectHandler` 且防重入；Disconnect 后事件不再触发
    自愈。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`Protocol/OpcUa/OpcUaDriver.cs` ConnectAsync
    成功后 `_session.KeepAlive += ...`；DisconnectAsync/Dispose 解绑；回调按 `e.Status`
    分类并用重连 handler 状态防重入。
- AC-2：会话保活重连（P1-2b，断网恢复）——网络闪断（会话未过期）走 `SessionReconnectHandler`
  原地重连保 Session 实例；会话真正过期才重建；断网→恢复后无需上层干预自动续采。
  - 行为：运行 `OpcUaDriverIntegrationTests` 断网恢复用例（需新增：连接→断开网络/停参考服务器→
    恢复→自动续采；会话未过期场景断言 Session 引用未替换）。预期 PASS（受测试服务器能力约束，
    见状态注记）。
  - 源码检查（仅源码检查，实现后回填行号）：驱动使用
    `new SessionReconnectHandler(...).BeginReconnect(session, reconnectPeriodMs, callback)`
    （第二参数为毫秒重连周期）；回调按 `ReferenceEquals(handler.Session, ...)` 分支"原地保住 vs 重建"。
- AC-3：订阅迁移恢复（P1-2c）——重连成功后订阅保持激活并续采：Transfer 优先、失败降级
  Recreate；不手写第二套迁移逻辑；重建后订阅未激活时走 `_gate` 内既有订阅路径重拉兜底。
  - 行为：运行 `OpcUaDriverIntegrationTests` 订阅恢复用例（需新增）：断网恢复后
    `IsSubscriptionActive == true` 且继续收到通知。预期 PASS（受测试服务器 DataChange 限制）。
  - 源码检查（仅源码检查，实现后回填行号）：`OpcUaDriver.cs` 不直接手写
    `TransferSubscriptionsAsync` 双迁移；重建完成核验 `IsSubscriptionActive`；回退仅调用既有
    `EnsureSubscriptionAsync`/`RecreateSubscriptionsAsync`（均在 `_gate` 内）。
- AC-4：状态对齐与健康单一权威（联动点）——重连窗口 `DriverState` 保持 `Connected`；
  成功维持/回到 `Connected`；最终失败才 `Faulted`/`Disconnected`；驱动/自愈不直改设备
  Online/Offline。
  - 行为：运行 `DriverState`/重连状态单测（需新增）与 `DeviceHealthMonitorTests`/
    `CircuitBreakerTests` 回归，预期全 PASS 且健康/熔断行为不变。
  - 源码检查（仅源码检查）：驱动与自愈代码无 `DeviceStatus.Online/Offline` 直改（设备健康只经
    既有上报入口）；`Device/DeviceHealthMonitor.cs`、`Collection/Resilience/CircuitBreakerHealthListener.cs`
    未在本次变更中修改（git diff 不含）。
- AC-5：生命周期清理——Disconnect/Dispose 停掉重连 handler、解绑 KeepAlive 再关会话；
  可重复 Disconnect/Dispose 幂等；会话置空后重连回调不再访问会话（无异常、无泄漏）。
  - 行为：运行生命周期单测（需新增）：重复 Disconnect/Dispose 不抛异常；handler 已 Dispose、
    事件已解绑。预期 PASS。
  - 源码检查（仅源码检查，实现后回填行号）：`OpcUaDriver.cs` Disconnect/Dispose 顺序为
    CancelReconnect/Dispose handler → 解绑事件 → 关会话；回调内不阻塞、不长时间持 `_gate`。
- AC-6：回退轮询兜底（D7）——自愈最终失败后采集回到既有自动建连 + 轮询路径，不静默停采。
  - 行为：运行 `ReliableProtocolDriverTests` / `SubscriptionCoordinatorTests` 既有回归
    （含订阅失败回退轮询用例），预期全 PASS。
  - 源码检查（仅源码检查）：不新增自愈专用恢复状态机；v1 轮询兜底与
    `SubscriptionCoordinator` 激活路径未被删除或绕过。
- AC-7：范围外未改动——本次变更仅限 Protocol/OpcUa（会话自愈）、测试、notes（ADR/AC/worklog）
  及任务链文件。
  - 验证：V-5 的 git 范围检查通过；`IProtocolDriver`/`ISubscriptionSource` 接口、Modbus/S7、
    健康监控/熔断文件、`RawPointValue` 无改动。

## 验证命令

- V-1：`dotnet build NitroGateway.slnx`；预期 0 Error、0 阻塞。
- V-2：`dotnet test tests\NitroGateway.UnitTests --filter "FullyQualifiedName~OpcUaDriverKeepAlive|FullyQualifiedName~OpcUaDriverState|FullyQualifiedName~ReliableProtocolDriverTests|FullyQualifiedName~DeviceHealthMonitorTests|FullyQualifiedName~CircuitBreakerTests"`；预期失败 0（含层3 新增用例）。
- V-3：`dotnet test tests\NitroGateway.IntegrationTests --filter "FullyQualifiedName~OpcUaDriverIntegrationTests"`；预期失败 0（断网恢复/订阅续采用例；受测试服务器 DataChange/可重启能力约束）。
- V-4：`dotnet test tests\NitroGateway.UnitTests --no-restore`；预期失败 0（回归）。
- V-5：`git status --short` 与 `git diff --stat`；预期变更仅含 Protocol/OpcUa 会话自愈文件、
  测试文件、notes（ADR-072/AC-layer3/worklog）与任务链文件，无范围外文件。

## 实测回填

2026-09-02 实现落地并执行验收（改动集中在 `Protocol/OpcUa/OpcUaDriver.cs`；新增
`tests/UnitTests/OpcUaDriverKeepAliveTests.cs`、集成用例
`ServerStop_KeepAliveSelfHeal_RecoversWithoutManualReconnect`）。逐条结论：

- **AC-1 KeepAlive 接入 —— PASS**
  - 行为：`OpcUaDriverKeepAliveTests` 12 用例全 PASS（V-2 含入）：Good/空状态不启动
    （`KeepAlive_GoodStatus_*`/`KeepAlive_NullStatus_*`）、Bad 启动一次
    （`KeepAlive_BadCurrentConnectedNoActive_StartsSelfHeal`）、防重入
    （`KeepAlive_BadWhileReconnectActive_*`）、旧会话迟到事件忽略
    （`KeepAlive_BadFromStaleSession_*`）、未连接不触发（`KeepAlive_BadWhenNotConnected_*`）。
  - 源码检查：`OpcUaDriver.cs:157` 连接成功即 `BindKeepAlive(session)`；
    `:656` `session.KeepAlive += OnSessionKeepAlive`；`DisconnectAsync:189-190` 与
    `Dispose:623-624` 先 `CancelReconnectHandler`/`UnbindKeepAlive` 再关会话；回调分类
    `OnSessionKeepAlive:688-`（Good 直接返回 `:691`，防重入位快速路径 `:695`）；
    纯判定 `ShouldStartSelfHeal:753-769`（Good/空、进行中重连、非当前会话、非 Connected
    均 false）。
- **AC-2 会话保活重连（断网恢复）—— PASS（读恢复 + 状态对齐，不依赖 DataChange）**
  - 行为：集成用例 `ServerStop_KeepAliveSelfHeal_RecoversWithoutManualReconnect` PASS：
    服务器硬断链后 15s 内 KeepAlive 触发自愈（`IsReconnectActiveForTesting == true`）、
    期间 `State == Connected`（D5）；同端口重启后**不调用 `ConnectAsync`**，30s 内读自动恢复
    （`State == Connected`）。说明：测试服务器整机停止 → 会话已超时，实测走的是 SDK 重建路径
    （非"原地保 Session 实例"分支），原地分支由引用判定单测覆盖、行为与 D3 一致。
  - 源码检查：`new SessionReconnectHandler(telemetry):729`、
    `BeginReconnect(current, SessionReconnectHandler.DefaultReconnectPeriod, OnReconnectComplete):732`
    （第二参数毫秒重连周期）；回调按 `ReferenceEquals(replacement, _session):802` 分支
    "原地保住 vs 重建（`:835-838` 替换引用 + 重绑 + 订阅核验）"。
- **AC-3 订阅迁移恢复 —— PASS（实现走 SDK 内置 Transfer→Recreate；"重建后续采通知"受测试服务器限制，沿用层2 结论）**
  - 行为：层3 集成用例覆盖断网→恢复后读取自动续采（PASS）；订阅重建后的 DataChange
    "继续收到通知"断言受测试服务器 `CustomNodeManager2` 不产通知限制，与层2 同一根因
    （干净 HEAD 复现，见 worklog），非本次实现缺陷。
  - 源码检查：驱动全文**无**手写 `TransferSubscriptions`/`RecreateSubscriptions` 调用
    （迁移复用 SDK `Session.Recreate` 内置 Transfer→Recreate）；`RealignSubscription:861-885`
    只做可观测核验：同一 `Subscription` 对象已随 Transfer 迁到新会话（`:868`）则保留激活；
    未保住则释放并交还既有 `EnsureSubscriptionAsync`（ADR-071 幂等，`_gate` 内）重建（`:878-884`）。
- **AC-4 状态对齐与健康单一权威 —— PASS**
  - 行为：`OpcUaDriverKeepAliveTests` 覆盖 D5（`EnterFaulted_NoSelfHealActive_SetsFaulted` /
    `EnterFaulted_SelfHealWindowActive_KeepsState`）；集成用例断言重连窗口 `State == Connected`；
    `DeviceHealthMonitorTests`/`CircuitBreakerTests` 回归 PASS（V-2），健康/熔断行为不变。
  - 源码检查：失败读/探测改走 `EnterFaultedIfNotSelfHealing:892-896`（自愈窗口内不置 Faulted，
    调用点 `:423/:434/:1008/:1017`）；`OpcUaDriver.cs` 无任何 `DeviceStatus.Online/Offline`
    直改（grep 0 命中），Online/Offline 仍唯一由 `DeviceHealthMonitor` 判定；
    `Device/DeviceHealthMonitor.cs`、`Collection/Resilience/CircuitBreakerHealthListener.cs`
    未在本次变更中修改（V-5 git diff 不含）。
- **AC-5 生命周期清理 —— PASS**
  - 行为：`Dispose_Twice_NoThrow`、`Disconnect_WhenNeverConnected_NoThrowAndStateDisconnected`
    单测 PASS（重复 Disconnect/Dispose 幂等不抛）。
  - 源码检查：`DisconnectAsync:187-200` 顺序 = CancelReconnect(handler) → UnbindKeepAlive →
    DeleteSubscription → CloseSession → 置 Disconnected；`Dispose:622-628` 每步独立 try/catch
    幂等；`CancelReconnectHandler:673-681`（`Interlocked` 置位 + CancelReconnect + Dispose 各自吞异常）；
    回调不阻塞、不长期持 `_gate`（有界等待 `:704/:817`，fire-and-forget `:781`）。
- **AC-6 回退轮询兜底 —— PASS**
  - 行为：`ReliableProtocolDriverTests` 回归 PASS（V-2），全量单测 821 PASS（V-4）；订阅/自愈
    未新增专用恢复状态机。
  - 源码检查：自愈失败/被取消路径仅清 `_reconnectActive` 后返回，交由既有
    `ReliableProtocolDriver`（Polly 自动建连）+ `SubscriptionCoordinator`（轮询兜底）——
    `HandleReconnectCompleteAsync:795-798`（无可用会话）、`:819-825`（等闸门失败）、`:828-833`
    （到达时已断开）。v1 轮询兜底未被删除或绕过。
- **AC-7 范围外未改动 —— PASS（V-5 通过）**
  - 变更仅限：`Protocol/OpcUa/OpcUaDriver.cs`（自愈）、`NitroGateway.Protocol.OpcUa.csproj`
    （InternalsVisibleTo 供测试）、`tests/UnitTests/OpcUaDriverKeepAliveTests.cs`（新增）、
    `tests/IntegrationTests/OpcUaDriverIntegrationTests.cs`（新增 1 集成用例 + 2 等待辅助）、
    notes（ADR-072 / AC-layer3 / worklog）。`IProtocolDriver`/`ISubscriptionSource` 接口、
    Modbus/S7、健康监控/熔断、`RawPointValue` 均无改动。

### V-1..V-5 真实输出

- **V-1** `dotnet build NitroGateway.slnx` → **成功，0 Error**（19 警告，全部为存量
  Desktop NU1701 / Fakes CS0067 等，非本次引入）。
- **V-2** `dotnet test tests\NitroGateway.UnitTests --filter
  "FullyQualifiedName~OpcUaDriverKeepAlive|FullyQualifiedName~OpcUaDriverState|FullyQualifiedName~ReliableProtocolDriverTests|FullyQualifiedName~DeviceHealthMonitorTests|FullyQualifiedName~CircuitBreakerTests"`
  → **失败 0，通过 41，总计 41**。
- **V-3** `dotnet test tests\NitroGateway.IntegrationTests --filter
  "FullyQualifiedName~OpcUaDriverIntegrationTests"` → **总计 10：通过 7，失败 3**。
  3 个失败均为依赖 DataChange 通知的存量层2 用例（`Subscription_Create_ReceivesInitialValues` /
  `Subscription_ServerValueChange_ReceivesNotification` /
  `Subscription_BadStatus_DoesNotProduceValue_AndRecoversOnGood`），超时等通知——已在干净 HEAD
  （不含本次改动）复现同样 3 个失败，判定为测试服务器模拟限制而非驱动回归；
  本次新增层3 自愈用例 PASS。
- **V-4** `dotnet test tests\NitroGateway.UnitTests --no-restore` → **失败 0，通过 821，总计 821**。
- **V-5** `git status --short` / `git diff --stat` → 变更文件仅在
  Protocol/OpcUa（驱动 + csproj）、两个测试文件、notes（ADR-072/AC-layer3/worklog）；
  无范围外文件（详见 AC-7）。
