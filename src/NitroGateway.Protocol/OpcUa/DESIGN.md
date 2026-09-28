# OPC UA 模块设计文档

> 状态：与当前实现同步（四层生产化封装已落地）。四层决策分别见
> ADR-070（层1 Browse）、ADR-071（层2 订阅）、ADR-072（层3 会话自愈）、ADR-073（层4 安全）；
> 验收标准见 `notes/AcceptanceCriteria/AC-opcua-layer1..4.md`。
> 能力矩阵/现状速查见 [README.md](README.md)。

## 定位

OPC UA 协议驱动，基于 `OPCFoundation.NetStandard.Opc.Ua.Client` SDK（1.5.378.156）。
实现三个能力接口：

- `IProtocolDriver`（`NitroGateway.Domain.Protocols`）— 连接生命周期与点位读写；
- `IBrowseableDriver` — 单层节点浏览（配置/导入工具用，采集引擎不调）；
- `ISubscriptionSource` — 服务端订阅推送（原始值来源）。

驱动不实现 OPC UA 协议本身（建会话、拼帧、加密、序列化均由 SDK 完成），只做业务映射、
生命周期管理、状态对齐与错误语义。

---

## 项目结构

```
NitroGateway.Protocol/OpcUa/
├── NitroGateway.Protocol.OpcUa.csproj   （仅引用 Abstraction；InternalsVisibleTo 测试探针）
├── OpcUaAddress.cs                      PointAddress 子类（四型 NodeId）
├── OpcUaAddressParser.cs                "ns=3;s=Temperature" ↔ OpcUaAddress
├── OpcUaDriver.cs                       IProtocolDriver + IBrowseableDriver + ISubscriptionSource
├── OpcUaDriverCapability.cs             能力声明
├── OpcUaSecurityParameters.cs           安全参数解析 + 端点选择（纯逻辑，可单测）
├── OpcUaValueCodec.cs                   Variant ↔ 领域值映射（纯逻辑，可单测）
├── OpcUaNodeIdCodec.cs                  OpcUaAddress/ExpandedNodeId ↔ NodeId/字符串（纯逻辑，可单测）
├── OpcUaBrowseCodec.cs                  DataType/AccessLevel → 展示字符串（纯逻辑，可单测）
├── OpcUaClientConfigurationFactory.cs   ApplicationConfiguration/UserIdentity 构建（纯逻辑，可单测）
├── OpcUaServiceCollectionExtensions.cs  模块 DI 扩展（注册地址解析器）
├── README.md
└── DESIGN.md

NitroGateway.Domain/Protocols/           接口（与 IProtocolDriver 同级，装饰器可共用）
├── IProtocolDriver.cs
├── IBrowseableDriver.cs（含 BrowseNode）
├── ISubscriptionSource.cs
└── DriverCapability.cs

NitroGateway.Protocol/Abstraction/
├── ReliableProtocolDriver.cs            可靠性装饰器（Polly 建连/重试，透传 Browse/订阅）
├── ProtocolDriverPool.cs                按设备复用长连接驱动
└── ProtocolDriverFactory.cs             统一用 ReliableProtocolDriver 包裹具体驱动
```

> 注：`IBrowseableDriver`/`ISubscriptionSource`/`BrowseNode` 已从 OpcUa 目录**下沉到
> `Domain.Protocols`**（ADR-070/071），使 `ReliableProtocolDriver` 装饰器与 `OpcUaDriver` 都能实现，
> 避免 Abstraction ↔ OpcUa 循环依赖。

---

## 职责边界

```
ModbusDriver 负责                  OpcUaDriver 负责
───────────────────────            ───────────────────────
ushort[] → int/float/bool          Variant → double/string/bool
Endian 转换                        类型映射（UA Type → .NET Type）
返回 RawPointValue{Value=25.3f}    返回 RawPointValue{Value=85.3}

Pipeline 只看到 double/int/string/bool，不知道底层是哪个协议
```

**边界原则**：协议解码在驱动内完成；缩放/死区/分发由 Collection 的
`IPointValuePipeline` / `IDataDispatcher` 负责。订阅与轮询是"原始值来源"不同，
**转换/死区/双写/转发语义必须共用唯一管道**（ADR-053/071）。

---

## 地址模型

### OpcUaAddress

```csharp
public sealed record OpcUaAddress : PointAddress
{
    public ushort NamespaceIndex { get; init; }   // ns=N，默认 0
    public string? StringId { get; init; }        // ns=3;s=xxx → "xxx"
    public uint? NumericId { get; init; }         // ns=2;i=1001 → 1001
    public Guid? GuidId { get; init; }            // ns=4;g=xxx
    public byte[]? OpaqueId { get; init; }        // ns=5;b=base64
}
```

不存裸 string，结构直接对应 OPC UA NodeId 规范；`OpcUaAddressParser` 解析/序列化双向一致。

### AddressParser

```
ns=3;s=Temperature  → { NamespaceIndex=3, StringId="Temperature" }
ns=2;i=1001         → { NamespaceIndex=2, NumericId=1001 }
ns=4;g={guid}       → { NamespaceIndex=4, GuidId=... }
ns=1;b=base64       → { NamespaceIndex=1, OpaqueId=... }
```

- 只按第一个 `;` 切两段：字符串标识符本身可能含 `;`（`ns=3;s=a;b`），否则破坏往返一致。
- `GetDistance` 恒返回 -1：OPC UA 没有"连续地址"概念，批量读按 NodeId 列表而非区间。
- **不支持 `nsu=`（URI 命名空间）**，仅 `ns=<数字>`；留 P3。

---

## 接口

### IProtocolDriver（Domain.Protocols）

```csharp
DriverState State { get; }
DriverCapability Capability { get; }
Task<OperationResult> ConnectAsync(CancellationToken ct = default);
Task<OperationResult> DisconnectAsync(CancellationToken ct = default);
Task<OperationResult> PingAsync(CancellationToken ct = default);
Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default);
Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default);
Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default);
Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default);
```

### IBrowseableDriver（ADR-070）

```csharp
Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default);

public sealed record BrowseNode
{
    public required string NodeId { get; init; }     // "ns=N;..."，可直接回填点位地址
    public required string Name { get; init; }
    public required string TypeName { get; init; }   // 变量节点："Int32"/"Float"/...；否则 ""
    public required bool IsVariable { get; init; }
    public required string Access { get; init; }     // "Read"/"ReadWrite"/"Write"/"None"；否则 ""
}
```

### ISubscriptionSource（ADR-071）

```csharp
event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived;
bool IsSubscriptionActive { get; }
Task<OperationResult> EnsureSubscriptionAsync(IReadOnlyList<DevicePoint> points, int publishingIntervalMs, CancellationToken ct = default);
Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default);
```

---

## 驱动能力

```csharp
public static class OpcUaDriverCapability
{
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        SupportsSubscription = true,
        SupportsBrowse = true,
        MaxBatchSize = 0   // 无限制
    };
}
```

`ReliableProtocolDriver` 装饰器透传 Browse/订阅能力：内层支持则转发，不支持则返回"协议不支持"。

---

## 会话生命周期

### 连接（`ConnectAsync`，`_gate` 内）

```
ConnectAsync
  ├── 幂等：State==Connected && _session!=null → 直接成功
  ├── 端点非空校验（否则 Faulted + Validation）
  ├── OpcUaSecurityParameters.Parse(Parameters) → 非法则 Faulted + Validation（400）
  ├── BuildConfiguration + Validate(ApplicationType.Client)
  ├── CheckApplicationInstanceCertificates → 失败 Faulted + SecurityConfigurationError（不降级 None）
  ├── DiscoverAndSelectEndpointAsync（GetEndpoints + 显式档位过滤，无隐式 None 回退）
  ├── Session.Create(..., BuildUserIdentity(requirement), ...)
  ├── State = Connected；BindKeepAlive(session)
  └── 异常映射：OCE→Timeout / ServiceResultException→Communication / 其它→Timeout
```

- **长连接**：驱动由 `ProtocolDriverPool` 按设备复用，不在每轮采集后断开；`DisconnectAsync` 由
  设备离线/变更/池释放触发。
- `ReliableProtocolDriver` 负责初始建连与断开后的自动建连（Polly 指数退避）；
  **自愈只接管"已连接后的断线"**，二者不抢道（ADR-072 D2）。

### 会话自愈（层3，ADR-072）

```
Session.KeepAlive 事件（SDK 保活线程）
  ├── e.Status Good/空 → 无动作
  ├── 已有活动重连（_reconnectActive）→ 忽略（防重入）
  ├── 有界等待 _gate(2s) 后复核：ShouldStartSelfHeal(status, session, _session, State, active)
  │     （非当前会话 / 未 Connected / 已有重连 → false）
  └── 启动 SessionReconnectHandler.BeginReconnect(session, DefaultReconnectPeriod(ms), callback)

重连完成回调（SDK 定时器/线程池线程，fire-and-forget 到后台）：
  ├── handler.Session == _session → 原地重连成功，Session 与订阅保留，仅记日志
  └── 会话已重建：有界等待 _gate(5s) → 替换 _session、BindKeepAlive、RealignSubscription
```

- **SDK 内置迁移**：`Session.Recreate` 内部已实现 Transfer→Recreate 降级，本项目**禁止手写第二套
  迁移逻辑**（防双 Transfer）。`RealignSubscription` 只做可观测核验：原 `Subscription` 对象已随
  Transfer 迁到新会话则保留，否则释放引用交回 `EnsureSubscriptionAsync` 幂等重建。
- **状态对齐（单一权威）**：重连窗口保持 `DriverState.Connected`；失败读/探测走
  `EnterFaultedIfNotSelfHealing`（自愈窗口内不置 Faulted）；**不直接改设备 Online/Offline**，
  健康判定仍唯一由 `DeviceHealthMonitor` 负责。
- **生命周期纪律**：`Disconnect`/`Dispose` 顺序 = `CancelReconnectHandler` → `UnbindKeepAlive`
  → `DeleteSubscriptionAsync` → `CloseSession` → `Dispose`（顺序不可反）；回调不阻塞、不长时间持
  `_gate`。自愈最终失败回退既有轮询兜底（D7）。

### 断开（`DisconnectAsync`）

先停自愈/解绑 KeepAlive、删订阅，再 `CloseSession` + `Dispose`，`State = Disconnected`。

---

## 连接安全（层4，ADR-073）

### 安全参数契约（`OpcUaSecurityParameters.Parse`）

从 `DeviceConnection.Parameters` 读（PascalCase）：

| 键 | 取值 |
|----|------|
| `SecurityPolicy` | `None`，或 SDK `SecurityPolicies` 常量名（`Basic128Rsa15`/`Basic256`/`Basic256Sha256`…），或完整策略 URI |
| `SecurityMode` | `None` / `Sign` / `SignAndEncrypt` |
| `UserName` / `Password` | 用户名密码（另一项缺失 → Validation） |

- 空值/类型错误/非法枚举/冲突组合 → `OperationResult.Validation`（400，绝不 500）。
- 键只在 OPC UA 连接时消费，Modbus/S7 忽略。

### 端点选择（`DiscoverAndSelectEndpointAsync` + `OpcUaSecurityParameters.SelectEndpoint`）

- SDK 无可传 `SecurityPolicyUri` 的 `SelectEndpoint` 重载 → 用 `GetEndpoints` 拉端点后**手工过滤**。
- **无隐式 None 回退**：None 仅当显式声明 `SecurityPolicy=None` 或 `SecurityMode=None` 才允许。
- 未声明任何档位 → **安全优先**：选非 None 中 `SecurityLevel` 最高者；若目标仅提供 None 端点
  → 明确配置错误（提示显式配置 None）。
- 无匹配端点 → `Validation`，错误消息附可用端点清单（策略/模式/安全级别）。

### 证书与凭据

- PKI 目录相对进程工作目录：`opcua/pki/{own,trusted,issuers,rejected}`；
  `AutoAcceptUntrustedCertificates=false`，未信任证书被 SDK 判 `BadCertificateUntrusted` 并写入
  `rejected`，经证书管理 API 移入 `trusted` 后重试。信任状态以 **pki 目录为唯一权威**，不入 SQLite。
- 应用证书生成/加载失败 → 显式 `SecurityConfigurationError`，不再静默降级 None。
- **凭据不落明文**：明文只存在"前端输入 → API → 宿主内存 → 建会话"的瞬时链路；`ConnectionParams`
  落 AES-256-GCM 密文（主密钥经环境变量注入，`ICredentialProtector` 在宿主侧，加解密**不在
  Protocol 模块内**）；驱动只消费内存明文。

---

## 读写语义

### ReadBatchAsync

```
地址解析：非法地址跳过该点位（记 Warning），其余合并为一次 session.ReadAsync
  ├── Bad 状态点位：跳过（不产伪值）
  ├── SourceTimestamp 缺失 → 本地 UtcNow 兜底
  ├── 全部地址非法 → Protocol 错误（配置问题，不置 Faulted）
  ├── 0 个有效结果 → EnterFaultedIfNotSelfHealing + Protocol
  └── 部分成功 → 返回成功子集 + Warning
空点位列表 → ProbeLinkAsync（读 ServerStatus 探链路）
```

### WriteAsync / WriteBatchAsync

- 写入按 `DevicePoint.DataType` 显式构造 `Variant`：**Float 必须发 `Single`**，若按 .NET 类型映射
  发成 `Double` 会 `BadTypeMismatch`（实测）。
- 批量写当前为逐点调用（任一失败即返回）。

### 交换类型映射

- 读：`Variant → 领域值`（`sbyte/short/int/long/ushort/uint/ulong/float/double/bool/string`；
  `float→double`；null→0.0）。
- 写：`DataType → Variant`（11 种领域类型显式映射，未知类型按 .NET 实际类型兜底）。

---

## Browse（层1，ADR-070）

- **单层非递归**：`parent` 缺省 = Objects 目录（`ObjectIds.ObjectsFolder`，i=85）；否则经
  `OpcUaAddressParser` 转 NodeId。
- `BrowseDirection.Forward` + `ReferenceTypeIds.HierarchicalReferences`（含子类型）+
  `NodeClassMask = Object|Variable`，`BrowseNext` 循环展开 `ContinuationPoint` 分页。
- 变量节点批量补读 `DataType` + `AccessLevel` → `BrowseNode.TypeName`（映射领域 11 种，其余 Unknown）
  与 `Access`（`Read`/`ReadWrite`/`Write`/`None`）。
- `NodeId` 用地址解析器同格式序列化（`ns=N;...`），可直接回填点位地址。
- **失败/超时不置 `Faulted`**（只读配置工具，不污染采集状态机）；非法父地址 → `Validation`。
- 调用链：`GET api/devices/{deviceId}/browse?parent=` → `IProtocolDriverPool.GetOrCreate` →
  `OpcUaBrowseController` → 前端 `PointList.vue` 懒加载树点选。

---

## 订阅（层2，ADR-071）

`EnsureSubscriptionAsync`（`_gate` 内）：

```
未连接 → Unavailable；空点位 → Validation
签名 = {publishingIntervalMs}|{point.Id:Address:ScanIntervalMs ...（按 Id 排序）}
签名未变且已有订阅 → 直接成功（幂等复用）
先 DeleteSubscriptionAsync（先删后建）
预解析全部地址（非法 → Validation，避免半成品订阅泄漏）
new Subscription(telemetry, new SubscriptionOptions {
    PublishingInterval = 全局采集间隔,
    PublishingEnabled = true,     // 默认 false，不置 true 服务端永不推送
    KeepAliveCount = 10, LifetimeCount = 30 })
每个 enabled 点位一个 MonitoredItem {
    StartNodeId = ToNodeId(address), AttributeId = Values.Value,
    SamplingInterval = ScanIntervalMs > 0 ? 点位间隔 : 发布间隔,
    QueueSize = 1, DiscardOldest = true }
item.Handle = point; item.Notification += OnMonitoredItemNotification
session.AddSubscription(subscription); await subscription.CreateAsync(ct)
```

通知路径（`OnMonitoredItemNotification`）：

- 解包 `args.NotificationValue`（SDK 1.5 实测为 `MonitoredItemNotification`，老版本为裸 `DataValue`），
  统一取 `DataValue`。
- 仅 `StatusCode.IsGood` 转 `RawPointValue`（Bad/Uncertain 跳过，不产伪值）；`SourceTimestamp` 缺失
  本地兜底。
- `PublishValuesAsync` 逐个 handler `await`，单个 handler 异常不影响其余。

采集侧接入：`SubscriptionCoordinator.TryActivateAsync` 订阅生效 → `DeviceCollector` 跳过本轮轮询；
失败/不支持返回 false → 保持轮询兜底（ADR-071 D2/D3）。

---

## DI 注册与驱动池

- 组合根 `AddNitroProtocol`（`ProtocolServiceCollectionExtensions`）以编译期 switch 映射协议名 →
  具体驱动：`"OPC UA" => new OpcUaDriver(connection, logger)`；工厂统一包 `ReliableProtocolDriver`。
- `ProtocolDriverPool` 以 `设备 ID + 连接参数指纹` 为键复用长连接；参数变化自动重建并驱逐旧驱动。
  **已知边界**：池只保证字典状态一致，`Evict`/`Dispose` 与在途读取之间仍有 use-after-dispose 窗口，
  靠"仅在设备离线/变更时驱逐"的上层约定规避（ADR-077）。
- `OpcUaServiceCollectionExtensions.AddNitroOpcUa` 注册 `OpcUaAddressParser`（驱动内部也自建解析器）。

---

## 错误语义

| 场景 | 返回 |
|------|------|
| 配置错误（端点空、安全参数非法、无匹配端点、地址全非法） | `Validation`（HTTP 400） |
| 服务级拒绝（证书未信任、认证失败） | `Communication`（消息含 SDK 状态码） |
| 连接不可达/超时/取消 | `Timeout` |
| 未连接调用读写/订阅/浏览 | `Unavailable` |
| 运行期读取/写入/订阅失败 | `Protocol`（读取失败复位 `Faulted`，自愈窗口除外） |
| 应用证书初始化失败 | `SecurityConfigurationError` |

所有操作返回 `OperationResult`，**不抛异常**（除显式取消）。

---

## 已知限制 / 未纳入

- `nsu=`（URI 形式命名空间）不支持，仅 `ns=<数字>`（P3）。
- 证书身份认证（`UserIdentity(CertificateIdentifier)`）未实现（P3 可选）。
- `RawPointValue` 未扩展 `ServerTimestamp`。
- 进程内参考服务器 `CustomNodeManager2` 不产生 DataChange 通知 → 层2 依赖推送续采的 3 个集成用例
  Timeout（干净 HEAD 可复现），非驱动缺陷。
- 不把 OPC UA 做成对外 Server，北向仍 MQTT。

---

## NuGet

`OPCFoundation.NetStandard.Opc.Ua.Client` 1.5.378.156 —— 不手写 OPC UA 协议。

> SDK 1.5 的经典同步 API（`Validate`/`SelectEndpoint`/`Session.Create`/
> `CheckApplicationInstanceCertificates`/`CloseSession`/`CertificateValidator()`）标记为
> `Obsolete` 但稳定可用，`OpcUaDriver.cs` 顶部统一 `#pragma warning disable CS0618`；
> 升级 SDK 大版本时再迁移异步 API。

## 约束

1. **唯一数据路径**：订阅通知必须经 `IPointValuePipeline` → `IDataDispatcher`，不得新增第二套。
2. **串行化**：同一 Session 的读写/Browse/订阅/断开均经 `_gate`。
3. **不产伪值**：Bad/Uncertain 不转值；读取失败返回错误而非假数据。
4. **安全不降级**：无隐式 None 回退；证书失败明确报错；密码不落明文。
5. **单一权威**：Online/Offline 只由 `DeviceHealthMonitor` 判定；驱动只维护自身 `DriverState`。
6. **复用优先**：会话自愈/订阅迁移复用 SDK `SessionReconnectHandler`/`Session.Recreate` 内置能力，
   禁止手写第二套。
7. **模块边界**：`Protocol.OpcUa` 只引用 Abstraction；加解密在宿主侧，Protocol 只消费内存明文。

---

## 变更记录

- v1（2026-08 定位）：轮询模式、无订阅、无自愈、无安全（None + 匿名）。
- 2026-09-01 层1 Browse（ADR-070）：`IBrowseableDriver` 下沉 Domain，单层浏览 + 前端点选。
- 2026-09-01/02 层2 订阅（ADR-071）：`ISubscriptionSource` + `SubscriptionCoordinator`，失败回退轮询。
- 2026-09-02 层3 会话自愈（ADR-072）：KeepAlive 接入 + `SessionReconnectHandler`，状态对齐、生命周期纪律。
- 2026-09-02 层4 安全（ADR-073）：显式安全档位、端点手工过滤、用户名密码、证书白名单、
  凭据加密落库、证书管理 API。
- 本次：README/DESIGN 同步到四层落地后的现状（此前 README 为缺口分析、DESIGN 停留在 v1）。
