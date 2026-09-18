# OPC UA 层1（基础通信）设计过程还原

- 日期: 2026-09-08 | 类型: 设计过程还原（reverse-engineered，非 ADR 决策记录）
- 还原依据:
  - 实现地真相：`src/NitroGateway.Protocol/OpcUa/OpcUaDriver.cs`（全文 1237 行）及其周边层1文件
  - 计划基线：`docs/07-OPC-UA四层生产化封装审查与实施计划.md`（层1 定义与 P0-1 Browse 封装设计）
  - 决策记录：`notes/ADR/protocol/ADR-070-opcua-layer1-browse.md`（层1 P0-1）
  - 初始设计：`src/NitroGateway.Protocol/OpcUa/DESIGN.md`（v1 快照）
- 还原方法：以"现状代码 = 唯一可信终态"为锚，倒推每一处实现背后的设计问题、候选与取舍。
  代码注释与既有文档标注为【证】，未见于文档的推断标注为【推】，避免把推断冒充史实。
  注：本文只还原**层1 基础通信**（连接/会话/读/写/浏览/地址模型/能力与错误契约）。
  层2 订阅（ADR-071）、层3 会话自愈（ADR-072）、层4 安全（ADR-073）在 `OpcUaDriver.cs`
  中与层1 交织，仅在影响层1 行为处（闸门、Faulted 语义、连接前置校验）说明其边界。

---

## 0. 层1 边界与现状锚点

docs/07 把 OPC UA 生产化封装切成四层，**层1 · 基础通信**覆盖的能力清单（docs/07 §2 层1 表）：

| 能力项 | 层1 归属 | 现状代码位置【证】 |
|---|---|---|
| Endpoint 选择 | 连接 | `ConnectAsync` → `DiscoverAndSelectEndpointAsync`（OpcUaDriver.cs:985） |
| Session / SecureChannel | 连接 | `ConnectAsync`（OpcUaDriver.cs:160）；SecureChannel 由 SDK 自动 |
| Namespace / 地址空间 | 地址模型 | `OpcUaAddressParser`（ns=`<数字>` 四型） |
| Browse 节点树 | 浏览 | `BrowseAsync`（OpcUaDriver.cs:525）+ ADR-070 |
| Read / Write | 读写 | `ReadBatchAsync` / `WriteAsync`（OpcUaDriver.cs:390/473） |
| DataType 映射 | 读写 | `VariantToValue` / `ToVariant`（OpcUaDriver.cs:1116/1139） |
| NodeClass / AccessLevel | 浏览 | `BrowseAsync` 变量补读 + `DataTypeName`/`AccessToString`（OpcUaDriver.cs:589/1212/1231） |

层1 的**成败标准**（docs/07 摘要）：基础通信约 80% 完成，唯一缺口 = **Browse 节点浏览 + 前端点选（P0-1）**；
同时必须满足"**仓库已有功能绝不自己重写**"的硬约束——SDK 已实现的协议/证书/会话全部复用，本项目只写业务封装。

---

## 1. 起点：v1 设计留下的形态与缺口（DESIGN.md 快照）

DESIGN.md（OpcUa 模块初始设计 v1）【证】给出最初的定位与约束：

- 定位：实现 `IProtocolDriver`，基于 OPC Foundation SDK 管理 Session 生命周期做 Read/Write；v1 轮询，v2 加订阅。
- **每轮一 Session**：`Connect → Read → Disconnect`（DESIGN.md §Session 生命周期 v1、§约束 6），不保活。
- **v1 无安全策略**：None Profile + 匿名（DESIGN.md §约束 1）。
- **Session 不自动重连**：断开返回 Error，上层重试（DESIGN.md §约束 3）。
- **Browse 独立接口**：`IBrowseableDriver` 与 `IProtocolDriver` 分离，采集引擎不调（DESIGN.md §约束 4）。
- 边界：`RawPointValue.Value` 已是领域值（double/int/string/bool），Pipeline 不做协议解码（DESIGN.md §职责边界）。

这一"能用"但粗糙的 v1 是后续层1 设计要解的问题集：短连接开销大、状态不可观测、
与上层采集/驱动池的复用契约（长连接）未定义、Browse 只留了接口没实现。

【推】由此推断层1 演进的目标不是"重做通信"，而是把 v1 的裸 Session 生命周期
改造成"能被驱动池复用的长连接 + 满足 OperationResult 契约 + 可安全并发的采集通道，
并把 Browse 缺口补上"。

---

## 2. 层1 设计约束（先定规则，后写代码）

从现状代码注释反推出的约束层（多数在类头 `<remarks>` 与 `_gate` 声明处【证】）：

1. **复用 SDK，不重写协议**：docs/07 §3 防重写清单——编码/握手/节点遍历/证书/加解密全部用 SDK。
2. **统一 `OperationResult` 契约，不抛异常**：`IProtocolDriver` 明确"所有操作返回 OperationResult"（IProtocolDriver.cs:10）。
3. **不破坏 `IProtocolDriver`**：docs/07 W1 约束卡禁止改公共接口；Browse 走独立 `IBrowseableDriver`。
4. **OPC UA Session 非线程安全 → 并发闸门**：全部通信（连接/断开/读/写/Ping/浏览）经 `_gate`
   串行化（OpcUaDriver.cs:26-28、48）。这是层1 与 Modbus/S7 驱动对齐的硬约束。
5. **状态机单一权威**：`DriverState{Disconnected/Connecting/Connected/Faulted}`（DriverState.cs）
   驱动自身只维护状态；**是否置 Faulted** 是层1 的关键语义开关，浏览/配置操作不置 Faulted（见 §7）。
6. **失败读不产伪值**：Bad/Uncertain 的 `WrappedValue` 是默认值，直接取会把故障当 0.0+Good 上云（ADR-019 P1-1，类头 remarks）。
7. **空点位设备也要探测链路**：否则"断开后仍 Connected 且无流量"= 假在线（ADR-031/ADR-019，ReadBatchAsync:402 分支）。
8. **地址模型与浏览输出同构**：`BrowseNode.NodeId` 序列化格式 = 地址解析器格式，点选可直接回填（ADR-070 D1/D7）。

---

## 3. 地址模型（层1 的地基）

### 3.1 决策：结构化 NodeId，不存裸 string

`OpcUaAddress`（OpcUaAddress.cs）对应 OPC UA NodeId 规范的四型标识符：
`NamespaceIndex`（ushort）+ `StringId` / `NumericId` / `GuidId` / `OpaqueId` 四选一。
【推】选择"强类型 record + init-only 属性"而非裸字符串，理由：
- 与 SDK `NodeId` 构造器一一对应（`ToNodeId` 四型 switch，OpcUaDriver.cs:1106），转换零成本、无二次解析；
- record 提供值相等，便于点位去重/签名比对（订阅签名复用同一思路）。

### 3.2 决策：解析/序列化对偶 + 只支持 `ns=<数字>`

`OpcUaAddressParser`（OpcUaAddressParser.cs）：
- `Parse` 支持 `s=`/`i=`/`g=`/`b=`，namespace 只接受 `ns=<ushort>`；非法地址抛 `ArgumentException`。
- `Serialize` 与 `Parse` 严格互逆，输出 `ns=N;...`。
- `GetDistance` 恒 -1：**OPC UA 没有"连续地址"概念**（OpcUaAddressParser.cs:72），与 Modbus 的地址连续性语义区分。

【证】ADR-070 D7 明确 `nsu=<URI>`（URI 形式命名空间）**暂缓不实现**，列 P3：
需会话内 NamespaceUris 反查 index，当前场景 `ns=` 足够。此边界直接决定 Browse 输出格式也用 `ns=<index>;...`。

### 3.3 决策：字段语义一致（读回填、写同样可解析）

读路径 `ReadBatchAsync` 把 `point.Address` 经 `_addressParser.Parse` → `ToNodeId`（OpcUaDriver.cs:413）；
写路径同样 `ToNodeId`（OpcUaDriver.cs:483）；浏览输出 `SerializeNodeId` 用 `ns=N;...` 同构（OpcUaDriver.cs:1196）。
三条路径共用一套地址语义 = 配置产物（Browse 点选）与采集消费（读/写）天然兼容，无需任何转换。

---

## 4. 连接生命周期（Connect / Disconnect / Ping）

### 4.1 决策：长连接取代"每轮一 Session"

【证】现状 `ConnectAsync`（OpcUaDriver.cs:82）不做"连一次读完就断"——`DisconnectAsync` 只在驱动被
驱逐/释放时由池调用；采集循环持长连接轮询。驱动由 `ProtocolDriverPool.GetOrCreate(device)` 提供
（ProtocolDriverPool.cs:22，键 = 连接指纹，参数变才重建）。【推】相对 DESIGN.md v1 短连接的核心动因：
采集 1s 周期若每轮握手+建会话，握手开销会淹没采样开销；且 Session 可缓存 Namespace/Server 能力，
长连接是工业采集的默认形态。

### 4.2 决策：`ReliableProtocolDriver` 装饰器管"建连+重试"，驱动管"通信"

驱动池返回的是 `ReliableProtocolDriver` 装饰器（ReliableProtocolDriver.cs:30）：
- `ReadBatchAsync` 走 Polly 管线：非 Connected 先自动 `ConnectAsync` → 超时读 → 指数退避重试（默认 500ms→1s→2s）。
- 单点读 `ReadAsync`、写 `Write`、浏览 `BrowseAsync` **透传内层不经 Polly**——重试只保护采集批量读这条主数据路径。
- 初始建连重试归装饰器；驱动内部只处理"已连接后的通信失败→置 Faulted"，交给上层再建连。

### 4.3 ConnectAsync 的设计序列（现状代码为终态的倒推）

`ConnectAsync` 内严格分步，每步失败映射不同 `OperationResult`（OpcUaDriver.cs:82-205）：

| 步 | 校验/动作 | 失败语义 | 代码 |
|---|---|---|---|
| 0 | 取闸门 + 二次检查"等待期间可能已连上" | 幂等成功 | :84-89 |
| 1 | Endpoint 非空 | `Validation`（配置错，非 500） | :91-95 |
| 2 | 安全参数契约解析（层4 ADR-073 D1） | `Validation`（含错误清单） | :97-105 |
| 3 | State=Connecting | — | :107 |
| 4 | 超时下限对齐：`Math.Max(1000, RequestTimeoutMs)` | 杜绝乐观超时先于设备超时触发 | :113 |
| 5 | 程序化构建 `ApplicationConfiguration`（无 XML）+ Validate | — | :116-117 |
| 6 | 应用证书初始化（层4；失败显式报错，不再静默降级 None） | `SecurityConfiguration` | :122-146 |
| 7 | GetEndpoints → 按显式档位手工 SelectEndpoint（无隐式 None 回退） | `Validation`（含可用端点清单） | :148-155 |
| 8 | `Session.Create`（updateBeforeConnect:false 不重复发现；checkDomain:false） | — | :157-169 |
| 9 | 绑定 KeepAlive（层3 ADR-072 自愈入口） | — | :175 |
| 10 | 异常映射 | 取消→`Timeout`；`ServiceResultException`→`Communication`（含 SDK 状态码）；其它→`Timeout` | :182-199 |

设计要点【证/推】：
- **Step 0 二次检查**：闸门等待不是重入锁，等待期间另一个连接请求可能已完成，需 `State==Connected && _session!=null` 短路。
- **错误分层（Validation vs Communication vs Timeout）**：配置错误（Endpoint 空/安全参数非法）不该伪装成通信超时；
  SDK 服务级拒绝（证书未信任/认证拒绝）映射 `Communication` 且消息带状态码，前端可区分（类头 remarks ADR-073）。
- **证书初始化"失败显式报错"**是演进产物：v1 是"尽力而为 + 静默降级 None+匿名"（DESIGN.md §约束1），
  层4 审查判定生产不可用后改为显式错误（docs/07 层4 P2-1a）。层1 连接由此吸收该校验为前置步骤。

### 4.4 DisconnectAsync：逆序清理，先停异步源再关会话

`DisconnectAsync`（OpcUaDriver.cs:208-233）顺序不可反：
1. 停层3 自愈重连 handler；2. 解绑 KeepAlive；3. 删订阅（层2）；4. 置 `_session=null`；
5. `CloseSession` + `Dispose`；6. State=Disconnected。
【推】顺序依据：自愈回调/保活事件若晚于 Session 关闭到达会访问已释放会话（空引用或协议失步），
故"停事件源 → 摘会话引用 → 关底层"的逆序是层1+层3 交互的既定协议。
`Dispose()`（OpcUaDriver.cs:649）同构幂等。

### 4.5 PingAsync：最小代价链路验证

读 `Server_ServerStatus` 节点（`VariableIds.Server_ServerStatus`），单点、`TimestampsToReturn.Neither`（OpcUaDriver.cs:347-376）。
【推】选 ServerStatus 而非自定义探针：它是 UA 内建节点、无副作用、代价最小，且不依赖用户地址空间。
空点位设备的采集读同样复用该探测（`ProbeLinkAsync`，OpcUaDriver.cs:1080）。

---

## 5. 读（ReadBatch / Read / 空点位探测）

`ReadBatchAsync`（OpcUaDriver.cs:390-470）是层1 采集主路径，设计分支：

1. **空点位 → ProbeLinkAsync**（:402-404）：发一次真实探测读验证链路，避免"断线仍 Connected + 无流量"假在线。
   空点位设备无数据点可读，若直接短路成功则在线状态失真；探测失败置 Faulted 让上层重连。
2. **地址解析失败逐点跳过**（:406-421）：单点非法记 Warning 跳过，不拖垮整批；但**全部非法 = 配置错误非通信故障**，
   返回 `Protocol` 且**不置 Faulted**（:424）。
3. **批量合并一次 Read**（:427）：`ReadValueIdCollection` 一条请求读所有合法点位，减少 RTT。
4. **Bad/Uncertain 不产伪值**（:433-438）：`StatusCode.IsBad` 则跳过该点并 Warning。
   SDK 在 Bad 时 `WrappedValue` 为默认值，直接取值 = 把故障当 0.0+Good 写时序库上云（ADR-019 P1-1）。
5. **时间戳兜底**（:444）：`SourceTimestamp==MinValue` 用本地 `UtcNow`。
6. **部分失败 vs 全部失败语义分离**：
   - 部分成功 → Warning 并返回已成功值（:456）；
   - **全部失败 → `EnterFaultedIfNotSelfHealing()`**（:450-454）→ 复位 Faulted，让上层重试管线整轮建连。
7. **层3 修正**：自愈重连窗口内不置 Faulted（保持 Connected，避免与层3 在同一断点"双车抢道"），
   `EnterFaultedIfNotSelfHealing`（OpcUaDriver.cs:921）即层3 ADR-072 D5 对层1 "全失败置 Faulted"规则的修订点。

单点 `ReadAsync` = `ReadBatchAsync([point])` 委托（:379-387），不单独实现，保证两条路径语义一致。

---

## 6. 写（Write / WriteBatch）

`WriteAsync`（OpcUaDriver.cs:473-510）：
- **按点位 `DataType` 强类型构造 Variant**（`ToVariant(DataType, value)`，:1139）：Webapi 写入值来自 JSON 数值一律
  double，若按 .NET 类型映射，Float 点会发成 Double → 服务端 `BadTypeMismatch`（ADR-019 实测）。
  Float 用 `Convert.ToSingle`、Int64 用 `Convert.ToInt64`…逐一强制。
- `WriteValueCollection` 单条写 → `session.WriteAsync` → 检查返回 StatusCode Good。
- 类型转换失败抛 `InvalidOperationException` 由上层捕获映射 `Protocol` 错误。

`WriteBatchAsync`（:513-522）**逐条 for 循环委托 WriteAsync**，未做协议级批量合并。
【推】写路径是低频、强校验操作，批次语义（部分成功/部分失败如何回执）复杂且写失败影响面大于读；
先保证单条正确性与幂等，批量合并收益低，故未按读路径做一次请求多值。

---

## 7. 浏览 BrowseAsync（层1 唯一显式缺口，P0-1 / ADR-070）

### 7.1 问题识别

docs/07 §2 层1：地址空间浏览为 ❌——接口 `IBrowseableDriver` + `BrowseNode` **已定义但 `OpcUaDriver` 未实现**，
后端无浏览 API，前端只能手填 NodeId（`ns=2;i=1001`），配置体验差（W1 评分 72 M）。
约束卡：必须实现 `BrowseAsync` 复用 SDK Browse/BrowseNext 不手写遍历；浏览只读、失败不置 Faulted 不打断采集；
分页+环检测；输出 NodeId 与 AddressParser 格式一致可直接填点；禁止改 `IProtocolDriver`、Storage、采集轮询路径。

### 7.2 决策与代码映射（ADR-070 D1~D7 × OpcUaDriver.cs）

| ADR 决策 | 代码落实【证】 |
|---|---|
| D1 Browse **单层非递归**，parent 缺省=Objects(i=85)；非法父地址→`Validation` 不置 Faulted | `BrowseAsync(parentNodeId="")`；缺省 `ObjectIds.ObjectsFolder`；非法父→`OperationalError.Validation`（:534-546, :637-640） |
| D2 复用 SDK，分页展开 | `BrowseDescription{Forward, HierarchicalReferences+子类型, NodeClassMask=Object\|Variable, ResultMask=DisplayName\|NodeClass\|TypeDefinition}`；`BrowseAsync` + while 循环 `BrowseNextAsync` 展开 ContinuationPoint（:550-587）。父节点不存在/无权限 → BrowseResult.StatusCode Bad → `Protocol` 错误不置 Faulted（:568-572） |
| D3 变量节点批量补读 DataType+AccessLevel → TypeName/Access | 过滤 Variable 节点 → 一次 `ReadAsync` 读 `Attributes.DataType`+`AccessLevel`（平行数组映射，避免 NodeId 键类型混用）→ `DataTypeName` 映射 11 种领域类型（Boolean/Byte/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String）→ `AccessToString` 映射 Read/ReadWrite/Write/None（:589-611, :1212-1236） |
| D4 浏览失败/超时不置 Faulted，在 `_gate` 内执行 | 整个 BrowseAsync 在 `_gate` 内（:528）；异常统一 `OperationalError.Protocol`（:637-640），注释明示"只读配置工具，不污染采集状态机" |
| D5 Webapi `GET /api/devices/{deviceId}/browse?parent=`，经驱动池取驱动、能力声明+类型双保险、Admin/Operator | 接口下沉 Domain（见下）；驱动池返回装饰器，装饰器转发（ReliableProtocolDriver.cs:214-219） |
| D6 前端懒加载树点选回填 Address/DataType/Access | 前端约定，本文档不复述 |
| D7 `nsu=` 暂缓 P3；输出用 `ns=<index>;...` | `SerializeNodeId` 四型输出 `ns=N;...`（:1196-1209），与地址解析器一致 |

### 7.3 关键架构决策：接口 + record 下沉到 Domain.Protocols

【证】`IBrowseableDriver` 现位于 `Domain/Protocols/IBrowseableDriver.cs`，头注释写明动因：
驱动池返回的是 `ReliableProtocolDriver` 装饰器实例，Browse 必须经装饰器转发到具体驱动才能复用长连接；
接口放 Domain 与 `IProtocolDriver` 同级后，装饰器与具体驱动（OPC UA）都能实现，**避免 Abstraction↔OpcUa 循环依赖**
（ADR-070 Rationale）。docs/07 §1 还可见迁移前它在 `Protocol/OpcUa/` 下——位置变化本身就是一次设计决策的痕迹。

### 7.4 环检测为何未实现

【证】docs/07 W1 约束卡要求"分页 + 环检测"，但 ADR-070 D1 收敛为**单层非递归**后，
单层浏览从定义上无环可检（每次只返回父节点一层 children，不沿引用下钻）；递归树改由前端懒加载按层请求。
环检测需求随"单层"决策一起消失——这是从计划（docs/07 递归表述）到 ADR（单层）再到实现的重要收窄。

---

## 8. 能力声明与契约（Capability / Registration）

- `OpcUaDriverCapability.Instance`（OpcUaDriverCapability.cs）：`SupportsBatchRead/BatchWrite=true`、
  `SupportsSubscription=true`（能力预留，层2 才真正用）、`SupportsBrowse=true`（层1 P0-1 补上）、`MaxBatchSize=0` 无限制。
- `DriverCapability.SupportsBrowse` 是层1 Browse 落地时新增的字段（DriverCapability.cs:19 注释 ADR-070 层次 1），
  Webapi 用它做"能力声明优先 + `is IBrowseableDriver` 双保险"（ADR-070 D5）。
- 注册键 `"OPC UA"` 与 `ProtocolIdentifier.OpcUa.Name` 一致（大小写不敏感但**区分空格**），
  不一致会导致工厂 Create 抛 NotSupportedException（OpcUaRegistration.cs:8-10）。
- SDK 1.5 兼容：类头 `#pragma warning disable CS0618`——经典同步 API 被标 Obsolete 但仍稳定可用，
  ASYNC 版需 `ITelemetryContext` 依赖注入代价高无额外收益，故统一压制告警（OpcUaDriver.cs:11-15）。

---

## 9. 设计过程的时间线重建（演进而非一次性设计）

综合 docs/07（审查基线）、ADR-070（2026-09-01）、DESIGN.md（v1 快照）、代码注释中
ADR-019/031/062 引用，重建层1 的设计演化路径【证为主，标注推】：

```
阶段 A · v1 裸驱动（DESIGN.md 快照）
   每轮 Connect→Read→Disconnect；None+匿名；Session 不自动重连；Browse 只有接口无实现
   ├─ 痛点：短连接握手开销大；状态不可观测；驱动池/长连接契约缺失

阶段 B · 长连接 + 驱动池 + 装饰器（现状主体）
   ProtocolDriverPool 按连接指纹复用驱动；ReliableProtocolDriver 自动建连+Polly 指数退避（只包 ReadBatch）
   └─ 新问题：长连接使"采集读 + Webapi 写 + 健康 Ping"并发访问同一非线程安全 Session

阶段 C · 并发闸门 + 失败语义加固（ADR-019 系列，代码大量注释引用）
   _gate 串行化全部通信；失败读不产伪值（Bad 跳过）；全部失败置 Faulted 交上层重连；
   超时对齐 RequestTimeoutMs（去 3s 乐观超时）；错误分层 Validation/Communication/Timeout；重试日志降 Debug
   └─ 层1 行为在此定型："置不置 Faulted"成为语义开关

阶段 D · 四层审查 + 层1 Browse 补缺（docs/07 2026-09-01 → ADR-070）
   审查判层1 约 80% 完成，唯一缺口 Browse P0-1；W1=72 M 约束卡；
   决策：单层+懒加载树、失败不置 Faulted、接口+record 下沉 Domain.Protocols、
   Webapi browse 端点、前端点选回填 Address/DataType/Access；nsu= 暂缓 P3
   └─ 层1 缺口闭合，能力声明补 SupportsBrowse

阶段 E · 后续各层在 OpcUaDriver.cs 上叠加并反向影响层1（ADR-071 订阅 / 072 自愈 / 073 安全）
   层3：KeepAlive 绑定挂进 ConnectAsync 尾部；EnterFaultedIfNotSelfHealing 修订层1 的"全失败置 Faulted"
   层4：连接前置加入安全参数契约校验与证书显式失败；SelectEndpoint 按显式档位（无隐式 None 回退）
   └─ 今日 OpcUaDriver.cs 是四层加固后的聚合体；层1 的连接/读路径被后三层注释引用最多的"既有约束"区
```

【推】阶段顺序的依据是相互引用的强弱：ADR-070 引用 ADR-019（层1 前置约束）而不反之；
代码把层3/层4 逻辑写成"修订层1 规则"的注释（如自愈窗口内不置 Faulted），说明层1 先于后三层定型。

---

## 10. 与 ADR-070 / docs/07 的一致性核对

逐条核对结果（还原终态 = 已实施 ADR-070）：

- [x] `OpcUaDriver : IBrowseableDriver` 已实现，`BrowseAsync` 存在（OpcUaDriver.cs:40, 525）。
- [x] 单层非递归、parent 缺省 Objects、非法父→Validation（OpcUaDriver.cs:534-546）。
- [x] 复用 SDK Browse/BrowseNext、分页 ContinuationPoint 循环展开（OpcUaDriver.cs:550-587）。
- [x] Variable 批量补读 DataType+AccessLevel→TypeName/Access（OpcUaDriver.cs:589-611）。
- [x] 浏览失败/超时置 Faulted？否——统一返回 OperationResult（OpcUaDriver.cs:637-640）。
- [x] 接口+`BrowseNode` 在 Domain.Protocols（IBrowseableDriver.cs）；装饰器实现转发（ReliableProtocolDriver.cs:214）。
- [x] `DriverCapability.SupportsBrowse` 与 `OpcUaDriverCapability.SupportsBrowse=true` 落地。
- [x] 输出 NodeId 用 `ns=<index>;...`，与解析器格式一致（SerializeNodeId）；`nsu=` 未实现（符合 D7）。

与 docs/07 层1 审查缺口表闭合：Read/Write/DataType/Endpoint/Session 本已完备；NodeClass/AccessLevel 随 Browse 补读；Browse 缺口关闭。

---

## 11. 遗留与未来边界（还原时读到的"仍开放"决策）

- `nsu=<URI>` 命名空间解析：P3，需会话内 NamespaceUris 反查 index（ADR-070 D7）。
- `MaxBatchSize=0`（无限制）但未定义超大批次保护；读路径一次合并全部点位，点位过多时是否分页未见约束。
- `OpcUaAddress.GetDistance=-1`：上层若做"连续地址区间批量生成"会对 OPC UA 失效（无此概念，语义正确，但调用方需感知）。
- SDK 1.5 经典同步 API（CS0618）依赖大版本升级后再迁移，属技术债显式标注（OpcUaDriver.cs:11-15）。
- WriteBatch 未做协议级批量合并（逐条写），若未来高频批量写出现，需要一次写多值 + 部分失败回执设计。

---

## 附：证据索引（设计结论 → 代码位置）

| 设计结论 | 证据位置 |
|---|---|
| 并发闸门串行化全部通信 | OpcUaDriver.cs:26-28, 48；每方法 `_gate.WaitAsync/Release` |
| 失败读不产伪值 | OpcUaDriver.cs:433-438（类头 remarks 引 ADR-019 P1-1） |
| 空点位设备探测 | OpcUaDriver.cs:402-404, 1080-1103 |
| 连接超时下限对齐设备 | OpcUaDriver.cs:113；ReliableProtocolDriver.cs:58-61 |
| 长连接复用 | ProtocolDriverPool.cs:22-54；IProtocolDriver 契约 |
| 自动建连+重试只在 ReadBatch | ReliableProtocolDriver.cs:165-199 |
| 地址四型 + ns= 数字 | OpcUaAddress.cs；OpcUaAddressParser.cs:14-51 |
| 浏览单层/缺省Objects/分页/补读/不Faulted | OpcUaDriver.cs:525-646 |
| Browse 接口下沉 Domain | IBrowseableDriver.cs:9-13（ADR-070 Rationale） |
| 能力声明补 Browse | DriverCapability.cs:19；OpcUaDriverCapability.cs:9-16 |
