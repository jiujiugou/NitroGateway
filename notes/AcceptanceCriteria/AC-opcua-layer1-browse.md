# 验收标准：ADR-070 · OPC UA 层1 Browse 节点浏览（P0-1）

> 范围：仅层1 P0-1（Browse 节点浏览 + 后端 API + 前端树点选）。P0-2 安全策略 / P1 订阅 /
> P2 证书 / P3 nsu= 不在本次范围。

## 驱动层（`OpcUaDriver` / `IBrowseableDriver`）

- [x] D-1 `OpcUaDriver` 实现 `IBrowseableDriver`（接口位于 `NitroGateway.Domain.Protocols`），
  提供 `Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default)`。
  **PASS**：编译通过且 `driver is IBrowseableDriver` 为 true。
- [x] D-2 `BrowseNode` 包含 `NodeId / Name / TypeName / IsVariable / Access` 五个字段，与 Webapi DTO 一一对应。
  **PASS**：`BrowseNodeDto` 字段与 `BrowseNode` 完全一致。
- [x] D-3 缺省父：`BrowseAsync("")` 浏览根目录（Objects，i=85），能列出 `ns=2` 命名空间下的
  Simulation 文件夹（`NodeId == "ns=2;i=5001"`、`IsVariable == false`、`TypeName/Access` 为空串）。
  **PASS**：集成测试 `Browse_Root_ReturnsSimulationFolder` 通过。
- [x] D-4 非法父：`BrowseAsync("not-an-address")` → `IsFailure` 且 `Error.Code == "ValidationError"`；
  驱动 State 仍为 `Connected`（不置 Faulted）；随后 `ReadAsync` 仍成功。
  **PASS**：集成测试 `Browse_InvalidParent_ReturnsValidationError_AndKeepsConnected` 通过。
- [x] D-5 变量字段：浏览 Simulation 文件夹（`ns=2;i=5001`）返回 4 个变量；
  `Int32Var`（`ns=2;i=1001`）`TypeName == "Int32"`、`Access == "ReadWrite"`；
  `FloatVar → "Float"`、`BoolVar → "Bool"`、`StringVar → "String"`。
  **PASS**：集成测试 `Browse_Folder_ReturnsVariablesWithTypeAndAccess` 通过。
- [x] D-6 未连接浏览返回 `Unavailable`（不抛异常、不产伪值），与读写路径一致。
  **PASS**：单测 `BrowseAsync_NotConnected_ReturnsUnavailable` 通过。
- [x] D-7 `OpcUaDriverCapability.SupportsBrowse == true`；`ReliableProtocolDriver` 装饰器实现
  `IBrowseableDriver`：内层支持时透传 parent、不支持时返回 "协议不支持节点浏览"。
  **PASS**：单测 `Capability_SupportsBatchAndSubscription` /
  `BrowseAsync_InnerNotBrowseable_ReturnsProtocolError` /
  `BrowseAsync_InnerBrowseable_ForwardsToInner` 通过。

## Webapi 层（`OpcUaBrowseController`）

- [x] W-1 存在 `GET api/devices/{deviceId}/browse?parent=`，返回 `ApiResponse<List<BrowseNodeDto>>`。
  **PASS**：`OpcUaBrowseController` 源码检查——`[HttpGet("{deviceId:guid}/browse")]` 路由存在；
  单测 `Browse_Connected_ReturnsMappedNodes` 断言返回 `ApiResponse<List<BrowseNodeDto>>` 通过。
- [x] W-2 设备不存在 → `404`（Error.Code == "NotFound"）。
  **PASS**：单测 `Browse_DeviceNotFound_Returns404` 通过。
- [x] W-3 非 OPC UA 设备（能力不支持浏览）→ `400`（"协议不支持节点浏览"，不建连）。
  **PASS**：单测 `Browse_NonOpcUaProtocol_Returns400` 通过。
- [x] W-4 未连接先 `ConnectAsync` 再浏览；建连失败 → `400`；浏览失败 → `400`（Message 携带原因）；
  浏览成功 → `200` 且 DTO 映射正确（parent 原样透传）。
  **PASS**：单测 `Browse_NotConnected_ConnectsFirst_ThenReturnsNodes` /
  `Browse_ConnectFails_Returns400` / `Browse_DriverBrowseFails_Returns400` /
  `Browse_Connected_ReturnsMappedNodes` 通过。

## 前端层（`PointList.vue`）

- [x] F-1 添加/编辑点位时仅 `deviceProtocol === 'OPC UA'` 显示地址输入框的"浏览"按钮。
  **PASS**：`PointList.vue` 源码检查——地址输入框 `#append` 模板 `v-if="deviceProtocol === 'OPC UA'"`；
  且 V-4 构建通过。
- [x] F-2 点击"浏览"弹出 el-dialog 内嵌 el-tree，懒加载（`:load="loadBrowseNode"`，根 parent=""）；
  变量节点（叶子）不显示展开箭头。
  **PASS**：`PointList.vue` 源码检查——el-dialog 内嵌 `el-tree lazy :load="loadBrowseNode"`（根 `parent=""`），
  `:props="{ isLeaf: 'isLeaf' }"` 且 `isLeaf: n.isVariable`（变量叶子不显示展开箭头）；且 V-4 构建通过。
- [x] F-3 点选变量叶子自动回填：`Address = nodeId`（`ns=N;...` 格式）、
  `DataType = typeName`（在领域支持列表内才回填）、`Access` 映射
  （Read→ReadOnly / ReadWrite→ReadWrite / Write→WriteOnly），并关闭对话框；
  点选非变量节点不动作。
  **PASS**：`PointList.vue` 源码检查——`onBrowseNodeClick` 仅对 `isVariable` 节点回填
  `Address=nodeId`、`DataType`（`types.includes(node.typeName)` 才回填）、`Access` 映射并关闭对话框，
  非变量节点直接 return 不动作；且 V-4 构建通过。
- [x] F-4 `npm run build`（vue-tsc 类型检查 + vite 构建）通过，0 类型错误。

## 验证命令

- [x] V-1 `dotnet build NitroGateway.slnx` → 0 错误。
- [x] V-2 `dotnet test tests\NitroGateway.UnitTests` → 全绿（791 通过 / 0 失败，含 Browse 单测）。
- [x] V-3 `dotnet test tests\NitroGateway.IntegrationTests` → 全绿（54 通过 / 0 失败，
  含 3 条 Browse 集成测试）。
- [x] V-4 `cd web && npm run build` → 通过（0 类型错误）。
