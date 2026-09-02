# ADR-071: OPC-UA 订阅采集与轮询共用采集管道（层 2 · P1-1）

- 日期: 2026-09-01 | 状态: 决策已定，实现+测试完成（V-3 通知用例待测试服务器修复）
- 来源: docs/07-OPC-UA四层生产化封装审查与实施计划.md 层2 P1-1；ADR-019（驱动闸门、不产伪值）、
  ADR-053（死区抑制）、ADR-062（点位级 ScanIntervalMs）、ADR-070（接口下沉 Domain 先例）

## Context

层 2 实时采集是 OPC UA 四层封装的最大缺口：`OpcUaDriver` 能力声明 `SupportsSubscription=true`
但采集引擎仍按全局轮询读取（W2 五因子 768 → XL，先立 ADR 再拆步实现）。OPC UA 服务端原生支持
Subscription/MonitoredItem 推送（SDK `Subscription`/`MonitoredItem`/`Notification`，已装包
1.5.378.156），可把采集从"每秒全量扫"降到"变化即推"，同时缓解单 Session 轮询读压力。
但订阅是**事件驱动的新数据路径**：不能绕过既有数据语义（缩放/死区/双写/转发/告警），
订阅不可用时也不能让采集停摆。

## Decision

- D1 **订阅以领域能力接口接入**：新增 `ISubscriptionSource`（`NitroGateway.Domain.Protocols`，
  与 `IProtocolDriver` 同级），契约 = `ValuesReceived` 事件 + `IsSubscriptionActive` +
  `EnsureSubscriptionAsync(points, publishingIntervalMs, ct)` + `StopSubscriptionAsync(ct)`。
  `OpcUaDriver` 实现该接口；`ReliableProtocolDriver` 装饰器透传转发；不支持订阅的协议不实现，
  采集行为不受影响。接口下沉 Domain 使装饰器与驱动都能实现，且不引入对 OPC UA 基础设施的依赖
  （与 ADR-070 D0 接口下沉同理）。
- D2 **协调器每设备管理订阅，尽力激活**：Collection 注册 Singleton `SubscriptionCoordinator`
  （`ISubscriptionCoordinator.TryActivateAsync(device, ct) → bool`）。订阅生效返回 true → 采集引擎
  跳过本轮轮询；启动失败/驱动不支持返回 false → 保持 v1 轮询兜底，不静默停采。
- D3 **通知复用唯一管道**：订阅 Good 通知 → `RawPointValue` → 既有 `IPointValuePipeline.Process`
  （缩放/死区/快照）→ `IDataDispatcher.DispatchAsync`（双写/转发/事件）。禁止第二套数据路径，
  保证订阅与轮询的数据语义（ADR-053 放行子集、ADR-062 降频、告警时长）完全一致。
- D4 **质量语义**：仅 `StatusCode.IsGood` 的通知转原始值；Bad/Uncertain 跳过不产值（ADR-019
  不产伪值底线）；`SourceTimestamp` 缺失本地兜底；`ServerTimestamp` 本轮不扩展 `RawPointValue`。
- D5 **间隔映射**：`DevicePoint.ScanIntervalMs` → MonitoredItem `SamplingInterval`
  （>0 用点位间隔，0 继承订阅发布间隔）；`Collection:IntervalMs` → Subscription
  `PublishingInterval`（与 ADR-062 语义对齐）。
- D6 **串行化与生命周期**：订阅创建/启停/删除全部在驱动 `_gate` 内（ADR-019，Session 非线程安全），
  与 Read/Write/Browse/Disconnect 串行；`Disconnect`/`Dispose` 删除订阅并解绑通知事件。
- D7 **订阅幂等复用**：点位与发布间隔未变（签名一致）时复用现有订阅，避免每轮重建订阅抖动；
  点位增删/间隔变化时先删后建。

## Alternatives

- A. 驱动回调直写存储：绕过 Pipeline/死区/双写/转发，数据语义分裂、质量与一致性不可控，否决。
- B. Collection 新建订阅专用分发链：与轮询两套管道，ADR-053 放行子集/告警时长要维护双份语义，否决。
- C.（选定）订阅以领域源事件接入现有 Pipeline/Dispatcher，失败回退轮询：保留唯一数据语义与消费路径，
  轮询作安全兜底，改动集中在协调器一层。

## Rationale

- 唯一数据路径原则：订阅与轮询只是"原始值来源"不同，转换/死区/分发语义必须同一套，否则死区抑制、
  双写、转发、告警会出现"轮询一套、订阅一套"的漂移。
- 尽力激活 + 回退轮询：`TryActivateAsync` 返回 bool 的契约让采集引擎接入改动最小（DeviceCollector
  一处判断），订阅失败是"可恢复的能力降级"而非故障，不改变既有熔断/健康判定模型。
- 接口在 Domain：驱动与装饰器都能实现 `ISubscriptionSource`，Abstraction/Collection 不依赖
  OPC UA 具体实现，保持模块边界。
- `_gate` 串行：OPC UA Session 非线程安全，订阅配置与读写并发会破坏会话，必须同闸门。

## Consequences

- OPC UA 设备订阅生效时由服务端推送驱动采集（发布间隔 = 全局采集间隔），网络流量与轮询压力下降；
  订阅失败/不支持自动回退轮询，采集不中断。
- `ScanIntervalMs > 0` 的点位在订阅路径同样按点位采样间隔生效（SamplingInterval），与轮询语义一致。
- 需随实现补齐：`SubscriptionCoordinator` 单测、驱动订阅集成测试（数据到达→Pipeline→双写→MQTT；
  Bad/Uncertain 不产值；启停/增删点位）、断连重建路径（层次 3 再处理 KeepAlive/Transfer）。
- 层次 3（会话自愈）、层次 4（安全/凭据）不在本 ADR 范围；Modbus/S7 路径、`IProtocolDriver`
  公共接口、`RawPointValue` 既有字段语义保持不变（只增不改）。

## 载荷墙（硬约束）

- 不得改 Modbus/S7 采集路径。
- 不得绕过 `IPointValuePipeline` / `IDataDispatcher`（禁止第二套数据路径）。
- 不得在订阅失败时静默停采（必须回退轮询，保留 v1 路径作兜底）。
- 同一 OPC-UA Session 的订阅配置与读写必须经驱动闸门串行化。
- 不改 `IProtocolDriver` 公共接口；`RawPointValue` 只可加字段、不改既有字段语义。

## 变更记录

- 2026-09-01 创建，决策定稿（订阅/轮询并存 + `ISubscriptionSource` 契约）。
- 2026-09-02 补充 D4 质量语义 / D6 串行化与生命周期 / D7 订阅幂等复用细则；
  状态更新为"决策已定，实现中（验收待回写）"。
- 2026-09-02 实现落地：`ISubscriptionSource`/`SubscriptionCoordinator`/驱动订阅支持/装饰器透传/
  DeviceCollector 跳过轮询/DI 注册，构建与单测全绿（809/809），V-3 通知用例待测试服务器修复
  （详见 worklog 2026-09-02 与 AC-opcua-layer2-subscription.md）。
