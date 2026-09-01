# 07 · OPC UA 四层生产化封装审查与实施计划

> 版本：v1.0 ｜ 日期：2026-09-01 ｜ 性质：审查 + 封装方案（审查产出，非实现记录）
> 依据：代码现状（`src/NitroGateway.Protocol/OpcUa/`、`Collection/`、`Domain/`、`Webapi/`、`web/`）+ OPC Foundation .NETStandard SDK `1.5.378.156`（已装包，API 经包内 XML 文档核对）
> 硬约束：**仓库已有功能绝不自己重写**——UA-.NETStandard SDK 已实现的协议、证书、会话、订阅、重连、加解密全部复用，本项目只做"业务封装"与"缺口的薄封装"。

---

## 0. 结论摘要

对四个层级的现状审查结论：**基础通信（层 1）约 80% 完成，实时采集（层 2）与安全（层 4）是主要缺口，工业可靠性（层 3）为"能恢复但粗粒度"（整轮重建 Session，未用 SDK 的会话保活/订阅迁移）**。

必须补的封装（按优先级）：

| 优先级 | 封装项 | 所属层 |
|---|---|---|
| P0-1 | Browse 节点树 + 前端点选（实现 `IBrowseableDriver.BrowseAsync`，SDK `Browse/BrowseNext`） | 层1 |
| P0-2 | SecurityPolicy / SecurityMode / 用户名密码可配置（`DeviceConnection.Parameters` → `SelectEndpoint` / `UserIdentity`） | 层4 |
| P1-1 | 订阅封装 + 采集引擎接入（SDK `Subscription`/`MonitoredItem`/`Notification`） | 层2 |
| P1-2 | 会话自愈（SDK `KeepAlive` + `SessionReconnectHandler` + `TransferSubscriptions`） | 层3 |
| P2-1 | 证书白名单 + 信任流程（去掉 `AutoAcceptUntrustedCertificates=true`） | 层4 |

每一项都在下文的"四层逐项审查"里给出：**SDK 已提供什么（复用点）→ 你的现状 → 必须补的封装**。

---

## 1. 现状盘点（代码实测）

| 文件 | 现状 |
|---|---|
| `Protocol/OpcUa/OpcUaDriver.cs` | 实现 `IProtocolDriver, IDisposable`；Connect/Disconnect/Ping/Read/ReadBatch/Write/WriteBatch 齐全；SDK 经典同步 API（`Session.Create` 等，`#pragma warning disable CS0618`）；匿名 `new UserIdentity()` 硬编码；`AutoAcceptUntrustedCertificates=true` + `CertificateValidation += Accept=true`；无 KeepAlive、无 Subscription |
| `Protocol/OpcUa/IBrowseableDriver.cs` | 接口 + `BrowseNode` record **已定义，但 `OpcUaDriver` 未实现该接口** |
| `Protocol/OpcUa/OpcUaAddress.cs` / `OpcUaAddressParser.cs` | NodeId 四型（s/i/g/b）解析/序列化完整；只支持 `ns=<数字>`，不支持 `nsu=<URI>` |
| `Protocol/OpcUa/OpcUaDriverCapability.cs` | `SupportsSubscription=true`（**能力预留，引擎实际仍轮询**）、`MaxBatchSize=0` |
| `Protocol/Abstraction/ReliableProtocolDriver.cs` | Polly 重试 + 指数退避 + 自动建连（`State != Connected → ConnectAsync`）；**重连=整轮重建 Session** |
| `Collection/DeviceReader/DeviceReader.cs` | 通过 `IProtocolDriverPool.GetOrCreate(device)` 复用长连接驱动 → `ReadBatchAsync` 轮询 |
| `Domain/Protocols/RawPointValue.cs` | 仅 `Point / Value / Timestamp`（SourceTimestamp），无 StatusCode/ServerTimestamp 字段 |
| `Domain/Devices/DeviceConnection.cs` | 有 `Parameters` 字典（可放用户名/密码/安全策略），未用 |
| `Domain/Devices/DevicePoint.cs` | 有 `ScanIntervalMs`（订阅采样间隔映射源）与 `Deadband` |
| `Webapi/Controllers/DevicesController.cs` / `PointImportController.cs` | 设备 CRUD / 点位 CSV 导入、批量生成；无 Browse 浏览 API |
| `web/src/views/Points/PointList.vue` / `Devices/DeviceForm.vue` | 点位手填 Address；协议下拉已有 OPC UA；无"从树选点位"入口；无安全/凭据表单 |

---

## 2. 四层逐项审查（SDK 已提供 / 你的现状 / 必须补的封装）

> 表头约定：**SDK 已提供** = UA-.NETStandard 现成能力（复用，不重写）；**现状** = 你目前封装到哪；**必须补** = 缺口要写的"业务封装"。

### 层 1 · 基础通信

| 能力项 | SDK 已提供（复用点） | 你的现状 | 必须补的封装 |
|---|---|---|---|
| Endpoint | `CoreClientUtils.SelectEndpoint` + `ConfiguredEndpoint.Update` | ✅ 已封装（含 None 回退） | 无（P0-2 改为可配置策略后这里要按策略过滤） |
| Session | `Session.Create` / `ISession` | ✅ 已封装 | 无 |
| SecureChannel | SDK 自动（transportChannel 握手） | ✅ 自动 | 无 |
| Namespace | `ISession.NamespaceUris` + `FetchNamespaceTablesAsync` | ✅ `ns=<数字>` 四型解析 | 可选：支持 `nsu=<URI>;s=Tag` 写法（现场常用），解析时按 NamespaceUris 反查 index |
| Address Space / Node | `SessionClient.BrowseAsync` / `BrowseNextAsync` + `BrowseDescription` + `ReferenceDescription` | ❌ 无（只能手填 NodeId） | **P0-1 Browse 封装**：`OpcUaDriver : IBrowseableDriver.BrowseAsync(parentNodeId)` 递归浏览，输出 `BrowseNode` |
| Read | `session.ReadAsync` | ✅ Read/ReadBatch | 无 |
| Write | `session.WriteAsync` | ✅ Write/WriteBatch | 无 |
| **Browse** | `BrowseDescription{NodeId, NodeClassMask, ResultMask}`、`BrowseNext`（分页 ContinuationPoint）、`ReferenceDescription{NodeId, BrowseName, NodeClass, TypeDefinition}` | ⚠️ 只有接口定义（`IBrowseableDriver`） | **P0-1**：实现 BrowseAsync（含分页展开 + 环检测）+ Webapi 浏览 API + 前端树点选 |
| DataType | `Variant` / `DataTypeIds` / `DataValue.WrappedValue` | ✅ 双向映射（Read `VariantToValue` / Write `ToVariant`） | 无 |
| **NodeClass** | `NodeClass` 枚举 + `ReferenceDescription.NodeClass` + 读 `Attributes.NodeClass` | ❌ 未读 | 随 **P0-1** Browse 输出 |
| **AccessLevel** | `AccessLevels`（CurrentRead/CurrentWrite/HistoryRead…）+ 读 `Attributes.AccessLevel` | ❌ 未读 | 随 **P0-1** Browse：对变量节点补一次 Read 属性 → `BrowseNode.Access`（Read / ReadWrite） |

**P0-1 封装设计（Browse）**：
```
OpcUaDriver : IBrowseableDriver
  BrowseAsync(parentNodeId = Objects, ct)
    ├─ ToNodeId(parent)（复用现有 OpcUaAddressParser）
    ├─ BrowseDescription { NodeId, BrowseDirection.Forward,
    │     NodeClassMask = Variable|Object, ResultMask = DisplayName|NodeClass|TypeDefinition }
    ├─ session.BrowseAsync(...) → BrowseResult（循环 BrowseNextAsync 展开 ContinuationPoint）
    ├─ 对每个 ReferenceDescription：
    │     └─ 变量节点 → 追加 Read(Attributes.Value 的 DataType 或 Attributes.AccessLevel) → BrowseNode
    └─ 返回 OperationResult<IReadOnlyList<BrowseNode>>（沿用现有 OperationResult 契约）
```
复用点全部在 SDK；你要写的是：地址转 NodeId、浏览循环、`ReferenceDescription → BrowseNode` 映射、以及 Webapi/前端这一层"业务壳"。

---

### 层 2 · 实时采集（当前最大缺口）

| 能力项 | SDK 已提供（复用点） | 你的现状 | 必须补的封装 |
|---|---|---|---|
| **Subscription** | `Subscription` + `SubscriptionOptions{PublishingInterval, KeepAliveCount, LifetimeCount, MaxNotificationsPerPublish}`、`session.CreateSubscription(options)`、`subscription.ApplyChangesAsync` / `CreateItemsAsync` | ❌ 未实现（能力声明 `SupportsSubscription=true` 但引擎轮询） | **P1-1a 订阅管理器**：`OpcUaSubscriptionManager`（每设备一个 Subscription），管理创建/启停/重建 |
| **MonitoredItem** | `MonitoredItem` + `MonitoredItemOptions{SamplingInterval, QueueSize, DiscardOldest, Filter=DataChangeFilter(死区)}`、`CreateMonitoredItem` / `AddItem` | ❌ 未实现 | **P1-1b**：每点位 `CreateMonitoredItem` → `AddItem` → `ApplyChangesAsync`；启动/停用点位增删 |
| **SamplingInterval** | `MonitoredItem.SamplingInterval` | ❌ 未映射 | `DevicePoint.ScanIntervalMs` → SamplingInterval（0 = 继承全局/最快），与 ADR-062 语义对齐 |
| **PublishingInterval** | `Subscription.PublishingInterval` | ❌ | 由全局采集配置（`Collection:IntervalMs` 或新增 OPC UA 配置）映射 |
| **DataChange** | `MonitoredItem.Notification` 事件 + `MonitoredItemNotificationEventArgs` → `DataValue` | ❌ 未接入 | **P1-1c**：事件回调 → `DataValue{Value, StatusCode, SourceTimestamp, ServerTimestamp}` → `RawPointValue` → 复用 `IPointValuePipeline`（缩放/死区/双写与轮询同一条管道） |
| StatusCode | `StatusCode.IsGood/IsBad/IsUncertain` | ⚠️ 仅 Bad 二元（Bad 跳过） | **P1-1d**：Uncertain 决策（跳过 or 映射 `QualityCode.Uncertain` 上行）；Bad 仍跳过不产伪值（ADR-019） |
| SourceTimestamp | `DataValue.SourceTimestamp` | ✅ 已取（缺失本地兜底） | 无 |
| ServerTimestamp | `DataValue.ServerTimestamp` | ❌ 未用 | 可选：`RawPointValue` 加字段或忽略（记录决策） |

**架构决策点（需 ADR）**：订阅推送与轮询共用 `IPointValuePipeline`，避免两套数据路径。建议新增 `ISubscriptionSource`（事件：点位值到达），由 Collection 引擎注册消费；订阅失败自动降级回轮询（v1 路径保留作兜底）。

---

### 层 3 · 工业可靠性

| 能力项 | SDK 已提供（复用点） | 你的现状 | 必须补的封装 |
|---|---|---|---|
| **KeepAlive** | `Session.KeepAlive` 事件（`SessionKeepAliveEventArgs{Status, LastGood, NonSecurePublishCount}`） | ❌ 未接入 | **P1-2a**：事件 → 区分"网络闪断（会话仍在）vs 会话丢失"→ 触发对应恢复路径 |
| 连接检测 | 读 `Server_ServerStatus` 节点 | ✅ `PingAsync` / `ProbeLinkAsync` | 无 |
| **Session 恢复** | `SessionReconnectHandler.BeginReconnect(ISession, maxRetries, callback)` / `Session.ReconnectAsync(ITransportWaitingConnection, ITransportChannel, ct)` | ⚠️ 有（ReliableProtocolDriver 整轮重建 Session，粗粒度） | **P1-2b**：网络闪断 → `SessionReconnectHandler` 保 Session 实例重连；会话真正过期才重建 |
| **Subscription 恢复** | `Session.TransferSubscriptionsAsync(subscriptions, sendInitialValues, ct)`（服务器保留订阅）/ `Session.RecreateSubscriptionsAsync(..., ct)` | ❌ | **P1-2c**：重连成功后先 `TransferSubscriptionsAsync`，失败降级 `RecreateSubscriptionsAsync` |
| **MonitoredItem 恢复** | 随订阅一起 Transfer/Recreate（SDK 自动），重连回调里重新 `AddItem`/`ApplyChanges` | ❌ | 随 **P1-2c** 一起 |
| Timeout | `TransportQuotas.OperationTimeout` | ✅ 与 `RequestTimeoutMs` 对齐 | 无 |
| Retry | `ReliableProtocolDriver`（Polly 指数退避） | ✅ | 无（保留做初始建连重试） |
| **Reconnect** | `SessionReconnectHandler` | ⚠️ 每次重建 Session（能连回来，但丢订阅、开销大） | **P1-2**：会话级自愈替换整轮重建（初始建连仍走 ReliableProtocolDriver） |

**联动点**：重连状态机必须与现有 `DriverState`、`CircuitBreaker`、`HealthReporter` 对齐（Online/Offline 判定单一权威来源在 `DeviceHealthMonitor`，别造第二套）。

---

### 层 4 · 安全

| 能力项 | SDK 已提供（复用点） | 你的现状 | 必须补的封装 |
|---|---|---|---|
| Application Certificate | `ApplicationInstance.CheckApplicationInstanceCertificates` / `CertificateFactory.CreateCertificate` | ⚠️ 尽力而为，失败**静默降级** None+匿名 | **P2-1a**：失败不再静默——返回明确错误（证书目录不可写/生成失败），前端给提示 |
| **Trust List** | `CertificateTrustList`（trusted/issuers/rejected 目录）+ `CertificateValidator.Update / ValidateAsync / GetRejectedListAsync` | ❌ `AutoAcceptUntrustedCertificates=true` + `CertificateValidation += Accept=true`（演示/内网，**生产不可用**） | **P2-1b 白名单校验**：关 AutoAccept；`CertificateValidation` 回调改为记录拒绝原因（BadCertificateUntrusted）→ 返回明确错误码 |
| 信任管理 | `CertificateStoreType.Directory` + rejected 目录 | ❌ 无操作入口 | **P2-1c 证书管理服务 + 前端**：读 rejected 列表 → "信任此服务器证书"（移入 trusted）→ 重试连接。现场互认流程文档化 |
| **SecurityPolicy** | `SecurityPolicies` + `CoreClientUtils.SelectEndpoint(..., useSecurity)`（可传 `SecurityPolicyUri` 过滤） | ⚠️ 自动选 + 硬回退 None | **P0-2a 策略可配置**：`DeviceConnection.Parameters["SecurityPolicy"]`（None/Basic256Sha256/…）→ 选端点；去掉隐含硬回退，改为显式"允许降级"开关 |
| **SecurityMode** | `MessageSecurityMode`（None/Sign/SignAndEncrypt） | ⚠️ 同上自动选 | **P0-2b**：`Parameters["SecurityMode"]` 配置 |
| Anonymous | `new UserIdentity()` | ✅ 默认 | 无（无凭据时保留） |
| **Username/Password** | `new UserIdentity(user, passwordBytes)` / `UserNameIdentityToken` | ❌ 硬编码匿名，`Parameters` 未用 | **P0-2c**：从 `DeviceConnection.Parameters["UserName"/"Password"]` 读 → 建会话传 `UserIdentity`；有凭据才用，否则匿名。前端 DeviceForm 加用户名/密码（后端不落明文，见"凭据安全"） |
| **Certificate Auth** | `UserIdentity(CertificateIdentifier)` / `CertificateIdentityToken` | ❌ | P3 可选（用户证书存储 + 私钥口令），非生产必选 |
| Sign / SignAndEncrypt | SDK 随证书/策略自动处理 | ⚠️ 依赖证书互认 | 不需要手写——依赖 P0-2（策略）+ P2-1（白名单）落地后自动生效 |

**凭据安全（涉及长期决策，写 ADR）**：`Parameters` 里的密码不得明文落 `appsettings.json`/DB；建议本地加密存储（如 DPAPI/机器级密钥）或环境变量注入，前端输入不回显。

---

## 3. 复用 vs 重写 对照（防重写清单）

| 你可能想手写的 | 实际该复用的 SDK 能力 |
|---|---|
| OPC UA 协议编码/握手 | `Opc.Ua.Core`（Channel/SecureChannel/序列化） |
| 节点树遍历 | `Session.BrowseAsync`/`BrowseNextAsync` + `BrowseDescription` |
| 订阅协议 | `Subscription`/`MonitoredItem`/`ApplyChangesAsync`/`Notification` |
| 会话保活/重连 | `Session.KeepAlive` + `SessionReconnectHandler` + `ReconnectAsync` |
| 订阅迁移 | `TransferSubscriptionsAsync` / `RecreateSubscriptionsAsync` |
| 证书生成/校验 | `ApplicationInstance` / `CertificateValidator` / `CertificateTrustList` |
| 加解密/签名 | SDK 随 `SecurityPolicy`/`SecurityMode` 自动 |

你要写的只有：**业务 DTO 映射、生命周期管理、与现有采集/告警/存储管道的对接、配置与 UI**。

---

## 4. 实施阶段（每阶段可独立验收）

| 阶段 | 内容 | 验收 | 档位（见 §5） |
|---|---|---|---|
| **P0** | ① Browse 节点树 + 前端点选；② 安全策略/用户名密码可配置（Parameters → SelectEndpoint/UserIdentity，前端表单） | 界面上从树选点位生成 Address；真实 Server 用 Basic256Sha256+用户名密码连上 | ① M ② M |
| **P1** | ① 订阅推送接入采集引擎（`ISubscriptionSource` + `OpcUaSubscriptionManager`，与轮询共用 Pipeline）；② 会话自愈（KeepAlive + SessionReconnectHandler + Transfer/Recreate） | 订阅模式持续上云；拔网线自动恢复且订阅不丢（Transfer 优先） | ① XL（拆步）② XL（拆步） |
| **P2** | 证书白名单 + 信任管理流程（关 AutoAccept，rejected→trusted + 前端操作 + 文档） | 无 AutoAccept 情况下连加密端点：首次给明确证书错误，加信任后成功 | XL（拆步） |
| **P3** | 证书身份认证（可选）；`nsu=` 命名空间解析（可选）；ServerTimestamp 字段（可选） | 按需 | — |

> 明确不做：不把 OPC UA 做成对外 Server（北向仍是 MQTT）；订阅只是 OPC UA 采集方式，Modbus/S7 仍轮询。

---

## 5. 逐项风险判定（problem-complexity skill 调用，开工前约束卡）

> 判定方式：五因子（规模/约束/故障影响/生命周期/耦合）各 1–5 分，乘积定档；任一因子 = 5 强制 ≥L。逐"封装工作项"判定，不对整个四层打一个分。

### W1 Browse 节点树 + 前端点选（P0-1）

| 因子 | 分 | 证据 / 风险点 |
|---|---|---|
| 规模 | 3 | Protocol driver + Webapi 新 API + 前端树组件；`BrowseNode` 为内部 DTO |
| 约束 | 2 | `IBrowseableDriver` 已定义不破坏；不碰 Storage/Abstraction 既有接口 |
| 故障影响 | 2 | 只读浏览，失败可返回错误，不影响采集链路与数据 |
| 生命周期 | 2 | 一次性配置工具，`BrowseNode` 非对外契约 |
| 耦合 | 3 | Protocol→Webapi→前端；浏览需临时取驱动（复用驱动池） |

**乘积 3×2×2×2×3 = 72 → M（中风险，明确重点防护）**

约束卡：
- [Y] 必须：`OpcUaDriver` 实现 `IBrowseableDriver.BrowseAsync`，复用 SDK `Browse/BrowseNext`，不手写遍历协议
- [Y] 必须：浏览只读、超时/失败返回 `OperationResult` 错误，不置 `Faulted`、不打断正在运行的采集会话（浏览走独立临时连接或与 `_gate` 串行并隔离异常）
- [Y] 必须：分页（ContinuationPoint）+ 环检测；返回的 `BrowseNode.NodeId` 与 AddressParser 序列化格式（`ns=..;s=..`）一致，可直接填点
- [X] 禁止：改 `IProtocolDriver` 公共接口、改 `Storage/`、动采集引擎轮询路径

### W2 订阅推送接入（P1-1，层 2）

| 因子 | 分 | 证据 / 风险点 |
|---|---|---|
| 规模 | 4 | Protocol 订阅管理器 + Collection 引擎对接 + 新抽象 `ISubscriptionSource` + 状态机 |
| 约束 | 3 | 适用 ADR-019（不产伪值）/053（死区）/062（点位级降频）；订阅与轮询语义并存；需 ADR 决策 |
| 故障影响 | 4 | 数据管道首环；订阅断开不补发 = 丢数据；状态机错 = 误报/漏报在线 |
| 生命周期 | 4 | 新增对外抽象 + 采集运行路径改变（影响后续所有 OPC UA 数据流） |
| 耦合 | 4 | Protocol→Collection→Pipeline→Persistence→Forwarder；事件驱动 + 轮询双路径 |

**乘积 4×3×4×4×4 = 768 → XL（极高风险，先降险再动工）**

约束卡：
- [Y] 必须：拆子步骤（订阅管理器 → 点位订阅 → Notification→Pipeline 接入），每步独立验证
- [Y] 必须：订阅数据与轮询共用 `IPointValuePipeline`（缩放/死区/双写），禁止第二套管道
- [Y] 必须：订阅失败自动降级轮询（v1 路径保留兜底），不静默丢数
- [Y] 必须：新增 ADR 记录订阅/轮询并存决策与 `ISubscriptionSource` 契约
- [Y] 必须：写恢复路径测试（订阅断开→重建→续采）与 Uncertain/ServerTimestamp 决策测试
- [X] 禁止：改动 Modbus/S7 采集路径；改 `RawPointValue` 既有字段语义（只可加字段）

### W3 会话自愈（P1-2，层 3）

| 因子 | 分 | 证据 / 风险点 |
|---|---|---|
| 规模 | 3 | OpcUaDriver 内部重构 + 重连状态机 + 与健康监控联动 |
| 约束 | 3 | 适用 ADR-019；`ReliableProtocolDriver` 语义对齐；`DriverState` 迁移对齐 |
| 故障影响 | 4 | 可靠性核心；做错会误判在线/离线、丢订阅数据 |
| 生命周期 | 3 | 驱动运行路径改变，影响采集可用性 |
| 耦合 | 3 | 驱动↔采集引擎↔`DeviceHealthMonitor`/`CircuitBreaker` |

**乘积 3×3×4×3×3 = 324 → XL（极高风险，先降险再动工）**

约束卡：
- [Y] 必须：拆子步骤（KeepAlive 检测 → 会话保活重连 → 订阅迁移），每步独立验证
- [Y] 必须：重连状态机对齐现有 `DriverState` + `DeviceHealthMonitor`（单一权威来源），禁止第二套在线判定
- [Y] 必须：初始建连重试仍走 `ReliableProtocolDriver`；自愈只接管"已连接后的断线"
- [Y] 必须：`TransferSubscriptionsAsync` 失败降级 `RecreateSubscriptionsAsync`；补断网恢复测试（拔网线→恢复→订阅续采）
- [X] 禁止：改 `IProtocolDriver` 公共接口；改健康监控/熔断对外行为

### W4 证书白名单 + 信任流程（P2-1，层 4）

| 因子 | 分 | 证据 / 风险点 |
|---|---|---|
| 规模 | 3 | OpcUaDriver 安全配置 + 证书管理服务 + Webapi + 前端证书操作 UI |
| 约束 | 3 | 生产不得 AutoAccept；现场互认流程文档化；涉及凭据/证书落盘 |
| 故障影响 | 4 | 安全做错 = 全连不上（证书卡死）或 不安全（静默降级 None）；生产关键 |
| 生命周期 | 3 | 安全行为改变，影响所有 OPC UA 设备连接 |
| 耦合 | 3 | Protocol↔Webapi↔前端；与配置存储/Persistence |

**乘积 3×3×4×3×3 = 324 → XL（极高风险，先降险再动工）**

约束卡：
- [Y] 必须：关 `AutoAcceptUntrustedCertificates`；`CertificateValidation` 改为记录拒绝原因 + 明确错误码（BadCertificateUntrusted），禁止 `e.Accept = true`
- [Y] 必须：提供 rejected→trusted 的管理入口（读 rejected / 信任 / 重试），前端可见可操作
- [Y] 必须：新增 ADR 记录证书目录/信任策略/凭据落盘方式（不落明文）
- [Y] 必须：补加密端点测试（首连拒绝 → 加信任 → 成功；None 显式配置才允许）
- [X] 禁止：隐式降级 None；明文存密码；改现有 Modbus/S7 安全行为

### W5 安全凭据/策略可配置（P0-2，层 4）

| 因子 | 分 | 证据 / 风险点 |
|---|---|---|
| 规模 | 2 | `Parameters` 读取 + OpcUaDriver 传 `UserIdentity`/选端点 + 前端表单 |
| 约束 | 3 | 凭据安全（不落明文）；兼容匿名；现场文档 |
| 故障影响 | 3 | 错配连不上，可回退 |
| 生命周期 | 2 | 一次性配置 |
| 耦合 | 2 | Protocol + 前端表单 |

**乘积 2×3×3×2×2 = 72 → M（中风险，明确重点防护）**

约束卡：
- [Y] 必须：有凭据用 `new UserIdentity(user, passwordBytes)`，无凭据才匿名；`Parameters` 空值校验（400 而非 500）
- [Y] 必须：密码不落明文（加密存储/环境变量注入），前端输入不回显
- [Y] 必须：`SelectEndpoint` 按配置策略过滤；None 仅显式配置才允许
- [X] 禁止：把密码写进 `appsettings.json`/DB 明文；隐含硬回退 None

---

## 6. 验证方案

- 复用现有进程内 OPC Foundation Server SDK（`OpcUaDriverIntegrationTests`/`SimulationServerScope`）扩展覆盖：
  - Browse：根→变量子树结构、分页、非法父节点错误；
  - 订阅：数据到达 → Pipeline 双写 → MQTT；订阅断开 → 重建 → 续采；
  - 自愈：拔网线/Server Stop → KeepAlive 触发 → Transfer 优先 → Recreate 兜底 → 回读；
  - 安全：None/加密端点两套；首连证书拒绝 → 信任 → 成功；用户名密码对/错。
- 回归：`dotnet build NitroGateway.slnx` + `dotnet test tests/NitroGateway.UnitTests` + 集成测试全绿。

---

## 7. 明确不做（砍掉，防范围蔓延）

1. 不把 OPC UA 做成对外 Server（北向保持 MQTT）。
2. 订阅只作为 OPC UA 采集方式；Modbus/S7 不引入订阅。
3. 证书身份认证（层 4 Certificate Auth）非生产必选，列为 P3 可选。
4. 不重写 SDK 已实现的任何协议/证书/订阅/重连能力（见 §3 防重写清单）。

