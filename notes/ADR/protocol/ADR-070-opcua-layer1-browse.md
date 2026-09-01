# ADR-070: OPC UA 层1 Browse 节点浏览封装（P0-1）

- 日期: 2026-09-01 | 状态: 已实施
- 来源: docs/07-OPC-UA四层生产化封装审查与实施计划.md 层1 P0-1；ADR-019（OPC UA 驱动并发闸门 `_gate`）

## Context

docs/07 审查结论：层1 基础通信约 80% 完成，唯一缺口是 **Browse 节点浏览 + 前端点选（P0-1）**。
现状 `OpcUaDriver` 实现 `IProtocolDriver`（Connect/Disconnect/Ping/Read/ReadBatch/Write/WriteBatch），
未实现浏览；`IBrowseableDriver` 接口与 `BrowseNode` record 原定义在 `NitroGateway.Protocol/OpcUa/` 下，
但 `OpcUaDriver` 未实现、后端无浏览 API、前端只能手填 NodeId（如 `ns=2;i=1001`），配置体验差（W1 评分 72 M）。

Browse 必须复用 SDK 的 `Session.BrowseAsync` / `BrowseNextAsync`（分页 ContinuationPoint），
返回沿用 `OperationResult` 契约；浏览是只读配置工具，失败/超时不应复位 `DriverState.Faulted`（不污染采集状态机）。
另：驱动池返回的是 `ReliableProtocolDriver` 装饰器，浏览必须经装饰器转发到具体驱动才能复用长连接，
因此接口与 `BrowseNode` record 需下沉到 `NitroGateway.Domain.Protocols`（与 `IProtocolDriver` 同级），
避免 Abstraction 与 OpcUa 之间循环依赖。

## Decision

- D1 Browse **单层非递归**：`OpcUaDriver : IBrowseableDriver.BrowseAsync(parentNodeId)` 只返回指定父节点下的
  一层 children；parent 缺省 = Objects 目录（`ObjectIds.ObjectsFolder`，i=85）；非法父地址走
  `OperationResult.Validation`（Error.Code = "ValidationError"），**不置 Faulted**。
- D2 复用 SDK：parent 地址经现有 `OpcUaAddressParser` / `ToNodeId` 转 NodeId；
  `Session.BrowseAsync` + `BrowseNextAsync` 循环展开 ContinuationPoint 分页；`BrowseDescription` 用
  `BrowseDirection.Forward`、`ReferenceTypeId=HierarchicalReferences`（含子类型）、
  `NodeClassMask = Object|Variable`、`ResultMask = DisplayName|NodeClass|TypeDefinition`。
- D3 变量节点补读属性：对 Browse 结果中的 Variable 节点批量一次 Read `Attributes.DataType` +
  `Attributes.AccessLevel` → `BrowseNode.TypeName`（DataTypeIds 映射领域支持的 11 种：
  "Bool"/"Byte"/"Int16"/"UInt16"/"Int32"/"UInt32"/"Int64"/"UInt64"/"Float"/"Double"/"String"）与
  `BrowseNode.Access`（CurrentRead/CurrentWrite → "Read"/"ReadWrite"/"Write"/"None"）。
- D4 浏览失败/超时**不置 `DriverState.Faulted`**，只返回 `OperationResult` 错误；BrowseAsync 在 `_gate`
  内执行，与读/写/Ping 串行（与 ADR-019 一致，OPC UA Session 非线程安全）。
- D5 Webapi：新增 `GET api/devices/{deviceId}/browse?parent=`，经 `IProtocolDriverPool.GetOrCreate(device)`
  取驱动（与 WriteService 同范式：未连接先 ConnectAsync、用后不断连，长连接留给采集复用）；
  能力声明 `SupportsBrowse` 优先 + `driver is not IBrowseableDriver` 双保险；非 OPC UA → 400；
  设备不存在 → 404；仅 Admin/Operator 可调（浏览是配置工具）。
- D6 前端：PointList 添加/编辑点位时仅 OPC UA 设备在地址输入框显示"浏览"按钮；el-dialog 内嵌 el-tree
  **懒加载**（`:load="loadBrowseNode"`，根 parent=""）；点选 Variable 叶子回填 `Address`（NodeId
  `ns=N;...` 与 AddressParser 序列化格式一致，可直接回填）、`DataType`（TypeName 在领域支持列表内才回填）、
  `Access`（Read→ReadOnly / ReadWrite→ReadWrite / Write→WriteOnly）。
- D7 nsu=（URI 形式命名空间）**暂缓不实现**：地址解析器目前只支持 `ns=<数字>`；Browse 输出用
  `ns=<index>;...` 与解析器保持一致。列为 P3 后续项（需会话内 NamespaceUris 反查 index）。

## Alternatives

- Browse 一次返回嵌套树（递归展开 children）：前端少请求，但服务端递归深、需环检测，响应大；
  与前端懒加载树（按需请求）相比更重。选单层 + 前端懒加载。
- 浏览直接调 `_session` 而非经驱动池装饰器：不复用长连接，每次请求建 Session，慢且占用连接资源；
  接口下沉 Domain 后装饰器可转发，复用 WriteService 同范式。
- D7 引入 nsu= 解析：需改地址解析器 + 会话内 NamespaceUris 反查 index，工作量大，当前场景 `ns=` 足够；留 P3。

## Rationale

- Browse 是只读配置工具，失败/超时是"配置输入问题"，与链路故障语义区分，不置 Faulted（ADR-070 D4）。
- 复用 WriteService 驱动池范式保证会话复用；BrowseAsync 在 `_gate` 内执行避免与采集读并发访问 Session。
- 变量节点补读 DataType/AccessLevel，使前端点选一次回填 Address/DataType/Access，避免用户再手填。
- 接口下沉到 Domain.Protocols 使 ReliableProtocolDriver 装饰器与 OpcUaDriver 都能实现，无循环依赖。

## Consequences

- OPC UA 点位配置可从树点选，NodeId 与解析器序列化格式一致可直接回填；层1 Browse 缺口闭合。
- `OpcUaDriver : IProtocolDriver, IBrowseableDriver`；浏览失败/超时不置 Faulted，不污染采集状态机。
- Webapi 新增浏览端点，仅 Admin/Operator 可用；`BrowseNode` → DTO 映射；非 OPC UA 协议返回 400。
- nsu=（URI 命名空间）留 P3 后续项，本次不实现。
