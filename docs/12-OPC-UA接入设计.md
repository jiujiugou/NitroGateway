# 12 · OPC UA 通信协议接入设计

> 状态：**已实施**（2026-08-19）。S1~S6 全部落地：build 0 错误、单元测试 695 通过（基线 663 + OPC UA 专项 32）、集成测试 45 全绿（含 OPC UA 冒烟 2）。OPC UA 冒烟 + 断链重连已用进程内 OPC UA Server（OPC Foundation Server SDK）实测通过；接真实 Prosys Simulation 降级为可选验收。

## 0. 三问（动手前）

- **为什么做**：① 市场求职要求（边缘网关/上位机 JD 普遍要求「至少两种主流协议」，默认指 Modbus + OPC UA / MQTT）；② 补齐协议栈——当前 `OpcUaDriver.ConnectAsync` 是空壳（直接抛 `NotImplementedException`），一问就露馅；③ 把「OPC UA 客户端」从源码存在变成本项目可演示的真协议。
- **验收标准**：可从设备管理界面注册 `OPC UA` 协议设备（注意名称含空格），点位地址填 NodeId 字符串（如 `ns=2;i=1001`），走通 采集 → SQLite → MQTT 全链路；build 0 错误、单测 695 全绿（基线 663 + OPC UA 专项 32）；断链/重连恢复（已实测）。
- **不做会怎样**：协议栈停在 Modbus+S7，OPC UA 永远是空壳；投边缘网关岗时「精通两种主流协议」这条无法自圆其说。

## 1. G1 确认

- 纯**新增**协议驱动，无破坏性操作；不触碰 `Storage/`、`Protocol/Abstraction/` 既有接口（符合「接口只增不删」）。
- **不新增依赖包**：`NitroGateway.Protocol.OpcUa.csproj` 已引用 `OPCFoundation.NetStandard.Opc.Ua.Client`（仅建议把 `Version="*"` 固定为具体版本，属依赖元数据调整，非升级）。
- 接入角色 = **采集侧 Client**（读别人的 OPC UA Server）；不做 OPC UA Server 对外暴露（北向仍是 MQTT）。

---
## 2. OPC UA 是什么（零基础 30 秒版）

- **一句话**：工业界的「统一接口/HTTP」——让不同厂家的设备/系统用**统一、自描述、带安全**的方式暴露数据。
- **两个角色**：`Server`（提供数据：SCADA/组态/上位机/某些 PLC/别的网关）↔ `Client`（取数据：本网关）。
- **数据组织**：一棵自描述的**节点树**（Address Space）。每个节点带 `Value + 质量 + 时间戳 + 单位/类型`，还可调用方法。
- **节点主键 NodeId 四型**（本网关点位地址直接用 NodeId 字符串，概念类似 Modbus 地址）：

  | 格式 | 含义 |
  |---|---|
  | `ns=2;i=1001` | 数字标识 |
  | `ns=3;s=Temperature` | 字符串标识（最常用，可读） |
  | `ns=4;g=xxx` | GUID 标识 |
  | `ns=5;b=xxx` | 二进制标识 |

- **两种取数**：轮询 `Read` / 订阅 `Subscription`（本接入 v1 用轮询，v2 再加订阅推送）。
- **安全**：内置证书/加密/签名。新手第一次连 OPC UA 常卡在「证书不受信任」——那是安全机制在起作用，不是代码写错。

## 3. 现状盘点（代码实测 2026-08-19）

### 3.1 已具备（约 80%）

| 项 | 文件 | 状态 |
|---|---|---|
| NodeId 四型地址模型 | `OpcUa/OpcUaAddress.cs` | ✅ 完整 |
| 地址解析 `ns=3;s=x` / `ns=2;i=1001` 等 | `OpcUa/OpcUaAddressParser.cs` | ✅ 完整 |
| 读/批读/写/批写/Ping/Dispose/`ToNodeId`/`VariantToValue` | `OpcUa/OpcUaDriver.cs` | ✅ 完整 |
| 能力声明（批量读写 + 订阅 + 无上限） | `OpcUa/OpcUaDriverCapability.cs` | ✅ `MaxBatchSize=0` |
| DI 注册（`AddNitroOpcUa` 注册解析器） | `OpcUa/OpcUaServiceCollectionExtensions.cs` | ✅ |
| SDK 依赖 | `NitroGateway.Protocol.OpcUa.csproj` | ✅ `Version="*"` |

### 3.2 原缺失（本轮已补齐）

| 项 | 现状 |
|---|---|
| `ConnectAsync` | ✅ 已实现：程序化 `ApplicationConfiguration`（PKI 目录 `opcua/pki/own\|trusted\|issuers\|rejected`）→ `CheckApplicationInstanceCertificates`（尽力而为，失败降级 None+匿名）→ `SelectEndpoint`（安全→None 回退）→ `Session.Create`（匿名身份、checkDomain:false） |
| 工厂注册 | ✅ 新增 `OpcUaRegistration.Register(factory)`，键 `"OPC UA"`（含空格，与 `ProtocolIdentifier.OpcUa.Name` 一致） |
| Protocols 工程引用 | ✅ `NitroGateway.Protocols.csproj` 已引用 OpcUa |
| 解决方案 | ✅ OpcUa 已入 `NitroGateway.slnx`，参与构建 |
| 测试 | ✅ 新增 `OpcUaAddressParserTests`（17）+ `OpcUaDriverTests`（9）+ 工厂注册冒烟（1），均通过 |
| 前端/设备协议 | ✅ `DeviceForm.vue` 协议下拉加 `OPC UA`，`syncParams` 三路分流（Modbus/S7/OPC UA），端点占位 `opc.tcp://127.0.0.1:4840` |

## 4. 架构定位

- 接入角色 = **采集侧 Client**，与 Modbus/S7 并列的**一个 driver**，走同一 `IProtocolDriver` 契约。
- 数据流不变：`DeviceReader → PointValuePipeline → SQLite → MQTT`。
- 白拿既有能力：复合工厂 / 驱动池（长连接复用、指纹重建）/ `ReliableProtocolDriver`（重试退避）/ 失败降级。

## 5. 实施步骤

### S1 工程引用
`NitroGateway.Protocols.csproj` 增加：
```xml
<ProjectReference Include="..\OpcUa\NitroGateway.Protocol.OpcUa.csproj" />
```

### S2 工厂注册（新增 `OpcUa/OpcUaRegistration.cs`，仿 `S7Registration`）
```csharp
public static class OpcUaRegistration
{
    public static void Register(ProtocolDriverFactory factory)
        => factory.Register("OPC UA", (_, conn, logger) => new OpcUaDriver(conn, logger));
}
```
> ⚠️ 注册键是 `"OPC UA"`（**含空格**），必须与 `ProtocolIdentifier.OpcUa.Name` 一致——工厂查找用 `OrdinalIgnoreCase`（忽略大小写但**不忽略空格**），键不一致 `Create` 会抛 `NotSupportedException`。

`NitroGateway.Protocols/ProtocolServiceCollectionExtensions.cs` 追加：
```csharp
OpcUaRegistration.Register(factory);
```
> 实际落点：`AddNitroProtocol()` 的工厂单例回调内，与 `ModbusRegistration`/`S7Registration` 并列调用；`OpcUaServiceCollectionExtensions.AddNitroOpcUa()`（地址解析器 DI）保持独立注册。

### S3 入解决方案
`NitroGateway.slnx` 在 `/src/NitroGateway.Protocol/` Folder 下增加 OpcUa 工程（与 Modbus/S7 同级）。

### S4 实现 `ConnectAsync`（核心，唯一真正工作）
- 用 `CoreClientUtils.SelectEndpoint(_connection.Endpoint)` 按 `opc.tcp://ip:port` 选端点。
- `SecurityPolicy`：先 `None` 或 `Basic256Sha256`，与 Prosys 模拟服务器对齐。
- `Session.Create(config, endpoint, false, "NitroGateway", timeout, ...)` 建会话。
- 证书：`AutoAcceptUntrustedCertificates=true` 已设；Server 侧把本机生成的 NitroGateway 应用证书加入信任（互认）。
- 失败归约 `OperationalError.Timeout`；`State` 迁移 `Connecting → Connected / Faulted`（骨架已写）。
- ✅ **进程内 OPC UA Server 冒烟实测调通**（OPC Foundation Server SDK 1.5.378.145 自起 Server，替代本机未安装的 Prosys；变量集对齐 Prosys 风格 `ns=2;i=1001` 等）。实测关键坑：Server 必须先 `CheckApplicationInstanceCertificates` 生成应用证书，否则 None 安全策略下 `CreateSession` 仍走 `ValidateDomains`，证书为 null → NRE → 被包装成 `BadUnexpectedError[80010000]`。

### S5 测试（新增，仿 Modbus 系）
- ✅ 单元 `OpcUaAddressParserTests`（17）：四型解析（s/i/g/b）+ 非法地址抛 `ArgumentException` + `Serialize` 往返一致。
- ✅ 单元 `OpcUaDriverTests`（9，失败路径，无需真实服务器）：初始 `State=Disconnected`；`Capability`（批量读/写+订阅）；未连接 `Read/ReadBatch/Write/WriteBatch/Ping → ResourceUnavailable`；空端点 `ConnectAsync → ValidationError`；未连接 `Dispose` 不抛。
- ✅ 注册冒烟：`ProtocolDriverFactoryTests` 加 `Create_OpcUa_Registered_ReturnsDecoratorWithCapabilities`——`OpcUaRegistration.Register` 后 `Create(ProtocolIdentifier.OpcUa, conn)` 返回 `ReliableProtocolDriver` 装饰器且 `Capability` 透传。
- ✅ 集成 `OpcUaDriverIntegrationTests`（2）：`SimulationServerScope` 动态端口 + 临时 PKI，`SimulationServer : StandardServer` 注入 `SimulationNodeManager : CustomNodeManager2`（ns=2 下 i=1001 Int32=42 / i=1002 Float / i=1003 Bool / i=1004 String）。覆盖 ① 连接→读→写（Int32/Float）→Ping→主动断开→重连→回读；② Server Stop→读失败（Faulted）→同端口重启→重连→回读。

### S6 前端/界面
- ✅ `DeviceForm.vue`：协议下拉加 `OPC UA`；传输方式显示 `opc.tcp`；端点占位/默认 `opc.tcp://127.0.0.1:4840`；`syncParams` 改三路分流（**关键坑**：原 else 分支把非 Modbus 全当 S7 处理，Rack/Slot 会污染 OPC UA）；`onProtocolChange` OPC UA 时 `dialect` 清空（后端 `ProtocolIdentifier.OpcUa` 无方言）；`loadS7FromParams` 仅 S7 回填。点位 `Address` 直接填 NodeId 字符串。

- ✅ `PointList.vue` 批量生成支持 OPC UA 起始地址（v2）：`defaultStartAddress` 按协议分流（'S7'→'DB1.DBD0'、'OPC UA'→'ns=2;i=1001'、默认→'40001'），新建/批量占位/设备切换三处统一走 helper；`genHint` 按协议展示递增规则（OPC UA：「地址按数值标识（i=）递增，如 ns=2;i=1001 → 1002，仅支持数值标识符」）。后端 `PointBatchService` 新增 OPC UA 递增分支（`ns={n};i={起始+i}`，仅 `i=` 数值标识，`s=/g=/b=` 抛 `ArgumentException`）+ 5 个单测。

## 6. 验证方案
- **环境（实际执行）**：进程内 OPC Foundation Server SDK（`OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.145）自起 Server，替代本机未安装的 Prosys；变量集对齐 Prosys 风格 `ns=2;i=1001` 等。
- **冒烟路径（已实测）**：驱动 Connect → 读初始值（Int32=42）→ 写 Int32/Float → 回读校验 → Ping → 主动 Disconnect → 重连 → 回读保留值。
- **断链/重连（已实测）**：Server Stop → 读失败（驱动 `Faulted`）→ Server 同端口重启 → `ConnectAsync` 重连 → 回读恢复。
- **验收标准**：build 0 错误；单测 695 全绿（基线 663 + OPC UA 专项 32）；集成 45 全绿（含 OPC UA 2）；冒烟全链路通；NodeId/时间戳/质量正确。
- **当前进度**：✅ 全部完成（2026-08-19）。接真实 Prosys Simulation Server 降级为可选验收（现场/后续，不影响本接入可用性）。

## 7. 风险与取舍

| 风险/取舍 | 说明 |
|---|---|
| SDK 体积 | OPCFoundation 依赖大，镜像/产物增大（完整度代价，可接受） |
| 证书互认 | 首次连接需双向加信任，现场要文档化处理 |
| 轮询 vs 订阅 | `SupportsSubscription=true` 但引擎是轮询；v1 轮询即可，v2 再评估订阅推送 |
| `MaxBatchSize=0`（无上限） | 注意与采集批量规划配合，防止一次读过多节点 |
| 质量/时间戳映射 | ✅ 已按 ADR-019 P1-1 处理：`ReadBatchAsync` 显式检查 `StatusCode`，Bad/Uncertain 跳过该点位不产伪值（SDK 在 Bad 时 `WrappedValue` 为默认值，直接取会把故障读当成 0.0+Good 入库上云）；`SourceTimestamp` 已取，缺失时本地时间兜底；`StatusCode → QualityCode` 细分映射留待 v2 |
| 雷区 | 不动 `Storage/`、`Protocol/Abstraction/` 既有接口；Domain 不引用基础设施（driver 在 Protocol 工程，符合） |

## 8. 面试要点（求职用途）

- 能讲概念：Server/Client、节点树、NodeId 四型、值+质量+时间戳、证书互认、轮询 vs 订阅。
- 能讲本接入：OPC UA 作为 driver 插进复合工厂，复用重试/驱动池/采集管道。
- 关键差异：OPC UA（建模/元数据/安全）vs Modbus（裸寄存器）的定位差异。

## 9. 决策与推进

- ✅ 已按用户「增加 opc 通信协议」指示实施完成（S1~S6），文档从设计稿转为**已实施**。
- 已联动更新：`docs/03-功能清单.md`（采集链路加多协议驱动条目）、`notes/worklog/2026-08-19.md`（实施记录）。
- 待办（可选）：接真实 Prosys Simulation 实测（本机未装、Docker 不可用，已用进程内 Server 覆盖同能力）；`docs/interview/protocol/` 补 OPC UA 题库。
