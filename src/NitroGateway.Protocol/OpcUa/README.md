# OpcUa

OPC UA 协议驱动实现，基于 `OPCFoundation.NetStandard.Opc.Ua` SDK（固定 1.5.378.156）。
实现三个能力接口：

- `IProtocolDriver` — 连接/断开/Ping/读写（统一协议接口）
- `IBrowseableDriver` — 节点浏览（层1，ADR-070）
- `ISubscriptionSource` — 服务端订阅推送（层2，ADR-071）

> 详细设计见 [DESIGN.md](DESIGN.md)；四层决策见 `notes/ADR/`（070/071/072/073）与验收标准
> `notes/AcceptanceCriteria/AC-opcua-layer1..4.md`。

---

## 当前状态：四层生产化封装已落地

| 层 | 内容 | ADR | 验收 |
|----|------|-----|------|
| 层1 基础通信 | Browse 节点浏览 + 前端树点选 | ADR-070 | ✅ AC-layer1 |
| 层2 实时采集 | Subscription/MonitoredItem 订阅推送，失败回退轮询 | ADR-071 | ✅ AC-layer2（3 个依赖 DataChange 的通知用例受进程内测试服务器限制，非驱动缺陷） |
| 层3 工业可靠性 | KeepAlive 触发会话自愈，复用 SDK 内置 Transfer→Recreate | ADR-072 | ✅ AC-layer3 |
| 层4 安全 | 显式安全档位、端点手工过滤、用户名密码、证书白名单、凭据加密落库 | ADR-073 | 实现已落地（端点/身份/证书/凭据保护/证书管理 API 均在代码中），验收回填见 AC-layer4 |

---

## 能力矩阵（SDK 提供 vs 本项目封装）

### 层1 基础通信

| 能力项 | SDK 提供（复用） | 本项目封装 |
|--------|------------------|-----------|
| Endpoint | `DiscoveryClient.CreateAsync` + `GetEndpointsAsync` | `DiscoverAndSelectEndpointAsync`（含显式档位过滤） |
| Session | `Session.Create` | `ConnectAsync` 建会话 + `UserIdentity` |
| SecureChannel | SDK TransportChannel 自动 | 无 |
| Namespace | `NamespaceIndex` | `OpcUaAddress` 四型 NodeId 解析 |
| Address Space / Node | Browse 服务 | `BrowseAsync` 单层浏览 |
| Read / Write | `session.ReadAsync` / `WriteAsync` | `ReadBatchAsync` / `WriteAsync` / `WriteBatchAsync` |
| DataType / NodeClass / AccessLevel | `Variant` / `NodeClass` / `AccessLevels` | Browse 变量节点补读属性 → `BrowseNode.TypeName/Access` |

### 层2 实时采集

| 能力项 | SDK 提供 | 本项目封装 |
|--------|----------|-----------|
| Subscription | `Subscription(TelemetryContext, SubscriptionOptions)` + `CreateAsync` | `EnsureSubscriptionAsync`（签名幂等复用） |
| MonitoredItem | `MonitoredItem(TelemetryContext, MonitoredItemOptions)` | 按 enabled 点位创建 |
| SamplingInterval | `MonitoredItem.SamplingInterval` | `ScanIntervalMs > 0` 用点位间隔，否则继承发布间隔 |
| PublishingInterval | `Subscription.PublishingInterval` | 由 `Collection:IntervalMs` 映射（`PublishingEnabled=true`，否则服务端不推送） |
| DataChange | `MonitoredItem.Notification` 事件 | 解包 `MonitoredItemNotification.Value` → `RawPointValue` → `ValuesReceived` |
| StatusCode | `IsGood/IsBad/IsUncertain` | 仅 Good 转值，Bad/Uncertain 跳过（不产伪值） |
| SourceTimestamp | `DataValue.SourceTimestamp` | 缺失时本地 `UtcNow` 兜底；`ServerTimestamp` 未扩展 |

### 层3 工业可靠性

| 能力项 | SDK 提供 | 本项目封装 |
|--------|----------|-----------|
| KeepAlive | `Session.KeepAlive` 事件 | `OnSessionKeepAlive` 事件分类 + `ShouldStartSelfHeal` 纯判定 |
| 连接检测 | 读 `Server_ServerStatus` | `PingAsync` / `ProbeLinkAsync` |
| Session 恢复 | `SessionReconnectHandler.BeginReconnect` | 闪断原地重连（保 Session 实例） |
| Session 重建 | `Session.Recreate`（内置 Transfer→Recreate） | 过期后由 SDK 重建，自愈层只做可观测核验 |
| Subscription 恢复 | 随 `Session.Recreate` 内置 | `RealignSubscription` 核验，未保住交回协调器重建 |
| 状态对齐 | — | 自愈窗口保持 `Connected`，失败才 `Faulted`（不造第二套 Online/Offline） |
| Timeout / Retry | `TransportQuotas.OperationTimeout` | 与 `RequestTimeoutMs` 对齐；重试由 `ReliableProtocolDriver` + Polly |

### 层4 安全

| 能力项 | SDK 提供 | 本项目封装 |
|--------|----------|-----------|
| Application Certificate | `CheckApplicationInstanceCertificates` | 失败显式 `SecurityConfigurationError`（不静默降级 None） |
| Trust List | `CertificateTrustList` / rejected 存储 | `opcua/pki/{own,trusted,issuers,rejected}`；`AutoAcceptUntrustedCertificates=false` |
| SecurityPolicy | `SecurityPolicies` 常量 | `OpcUaSecurityParameters.Parse`（短名/URI 解析） |
| SecurityMode | `MessageSecurityMode` | 同上（None/Sign/SignAndEncrypt） |
| 端点选择 | `GetEndpoints` | 手工按策略/模式过滤；**无隐式 None 回退，None 仅显式声明** |
| Anonymous / Username-Password | `UserIdentity()` / `UserIdentity(user, byte[])` | `OpcUaClientConfigurationFactory.BuildUserIdentity`：有凭据用账号，否则匿名 |
| 凭据落库 | —（宿主侧） | 明文只在内存；`ConnectionParams` 落 AES-256-GCM 密文，主密钥环境变量注入 |
| 证书信任管理 | `ICertificateStore` | `OpcUaCertificateManager` + rejected→trusted API |

---

## 关键行为要点

- **连接语义**：长连接驱动由 `ProtocolDriverPool` 按设备复用；`ConnectAsync` 幂等（已连接直接成功），
  `ReadBatchAsync` 未连接返回 `Unavailable`。
- **串行化**：同一 Session 的 Connect/Read/Write/Browse/订阅/Disconnect 全部经驱动 `_gate` 串行
  （OPC UA Session 非线程安全，ADR-019）。
- **订阅幂等**：点位集合 + 发布间隔构成签名，未变则复用现有订阅；变化时先删后建。
- **回退轮询**：订阅激活失败或驱动不支持时，`SubscriptionCoordinator` 返回 false，采集保持轮询。
- **错误语义**：所有操作返回 `OperationResult` 不抛异常；配置错误 → `Validation`（400），服务级拒绝
  （证书未信任/认证失败）→ `Communication`，连接不可达 → `Timeout`；Browse 失败不置 `Faulted`。

## 已知限制 / 未纳入

- **nsu=（URI 命名空间）不支持**：地址解析仅支持 `ns=<数字>`；留 P3。
- **证书身份认证未实现**：仅匿名 / 用户名密码；`UserIdentity(CertificateIdentifier)` 留 P3 可选。
- **`ServerTimestamp` 未暴露**：`RawPointValue` 只带 `SourceTimestamp`。
- **测试服务器限制**：进程内参考服务器 `CustomNodeManager2` 不产生 DataChange 通知，层2 的 3 个
  "推送续采"集成用例 Timeout（干净 HEAD 可复现），非驱动缺陷。
- **不做 OPC UA Server**：北向仍为 MQTT。

## NuGet

`OPCFoundation.NetStandard.Opc.Ua.Client` 1.5.378.156 —— 建会话、拼帧、加密、序列化全部由 SDK 处理，
本项目不手写 OPC UA 协议。

## 注意

SDK 1.5 将 `Validate` / `SelectEndpoint` / `Session.Create` / `CheckApplicationInstanceCertificates` /
`CloseSession` 等经典同步 API 标记为 `Obsolete`，但仍稳定可用且为当前主推兼容路径；`OpcUaDriver.cs`
顶部统一 `#pragma warning disable CS0618`，升级 SDK 大版本时再迁移异步 API。
