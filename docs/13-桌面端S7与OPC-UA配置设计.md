# 13 · 桌面端 S7 与 OPC UA 配置设计（仿 web DeviceForm/PointList）

> 状态：**已实施**（2026-08-21，文档先行，后修改）。S1~S5 全部落地：`dotnet build NitroGateway.slnx` 0 错误；`dotnet test tests/NitroGateway.UnitTests` 726 通过（基线 705 + 新增 21）0 失败。

## 0. 三问（动手前）

- **为什么做**：桌面端（WPF）设备/点位配置目前只支持 Modbus + S7，且 S7 的「传输方式」可任意改成 RTU（S7 只有 TCP，改错必连不上）；**完全没有 OPC UA**（后端 `OpcUaRegistration` 已注册、驱动可测，桌面表单却选不了）。web 端 `DeviceForm.vue` / `PointList.vue` 已按 ADR-024 / 12-OPC-UA接入设计.md S6 完成三协议分流与协议感知点位/批量生成，桌面端落后。
- **验收标准**：桌面端设备表单协议下拉含 `Modbus / S7 / OPC UA`；传输方式协议感知（Modbus=TCP/RTU 可编辑，S7=锁定 TCP，OPC UA=锁定 opc.tcp 且不落库）；端点默认值/占位按协议联动（S7 `IP:102`、OPC UA `opc.tcp://127.0.0.1:4840`）；点位地址提示与校验按协议（S7 `DB1.DBD0`、OPC UA `ns=2;i=1001`）；点位窗口新增「批量生成」（起始地址按协议给默认 + 递增规则提示）。单测新增用例全绿、`dotnet build NitroGateway.slnx` 0 错误、全量单测不回归。
- **不做会怎样**：桌面端 OPC UA 永远选不了、S7 配置继续可能被用户改成 RTU 而连不上；双端能力矩阵（docs/11）里「点位批量生成」对桌面的 ✗ 缺口继续存在；面试讲「双端全栈」时桌面侧 OPC UA 一问就露。

## 1. G1 确认（行为变更）

- **纯桌面 UI 行为变更 + 新增表单协议项**，无破坏性操作。
- **不动** `Storage/`、`Protocol/Abstraction/` 既有接口，不动 Domain/数据模型，不新增/升级依赖包（批量生成复用既有 `PointBatchService.Generate`，已在 Device 模块实现三协议递增）。
- 桌面**内部**接口 `IPointsViewModelFactory.Create` / `IDeviceDialogService.ShowPoints/EditPointBatch` 增加协议参数（非 Storage 接口，按调用点全量同步修改）。
- 传输方式从「任意 TCP/RTU」改为「协议感知」：**S7 锁定 TCP**、**OPC UA 锁定 opc.tcp（不写 dialect）**——这是对既有桌面行为的收紧，属行为变更，按 G1 先说明后动手。

## 2. 现状对比（web vs 桌面，代码核验 2026-08-21）

| 项 | web（参照物） | 桌面（现状） | 差距 |
|---|---|---|---|
| 协议下拉 | `Modbus / S7 / OPC UA`（`DeviceForm.vue`） | `Modbus / S7`（`DeviceEditorWindow.xaml`） | **缺 OPC UA** |
| 传输方式 | Modbus→TCP/RTU；S7→`TCP` 禁用；OPC UA→`opc.tcp` 禁用 | 恒为可编辑 TCP/RTU 下拉，S7 也能选 RTU | **非协议感知** |
| 端点默认/占位 | S7 `IP:102`、OPC UA `opc.tcp://127.0.0.1:4840`、Modbus `IP:502`，切协议自动换默认 | 无占位，不联动 | **缺** |
| 参数分流 | `syncParams` 三路分流（Modbus/S7/OPC UA），切协议清残留参数 | `ToDevice` 两路（Modbus/S7），无 OPC UA | **缺第三路** |
| S7 参数 | Rack(0-7)/Slot(0-31)/CpuType/PingAddress，范围由 el-input-number 约束 | 有，但 TextBox 无范围校验 | 部分 |
| OPC UA 参数 | 无协议特有参数，dialect=null | 无 OPC UA | **缺** |
| 点位地址提示 | 新建点位 `defaultStartAddress(protocol)` 给占位 | ToolTip 仅 `Modbus 如 40001 / S7 如 DB1.DBD0` | 缺 OPC UA、不随协议 |
| 点位批量生成 | `PointList.vue` ⚙ 批量生成（起始地址按协议 + 递增规则提示） | **无入口**（docs/11 标注 ✗） | **缺** |
| 连接测试 | `/devices/test-connection`（同工厂驱动） | `DeviceConnectionTester`（同工厂驱动） | 对等；OPC UA 已注册可测 |

## 3. 设计

### 3.1 设备表单（`DeviceEditor.cs` + `DeviceEditorWindow.xaml`）

对齐 web `DeviceForm.vue` 的三协议分流：

1. **协议下拉**：`Modbus / S7 / OPC UA`。
2. **传输方式（协议感知）**：
   - Modbus：可编辑 `TCP / RTU`；
   - S7：锁定 `TCP`（禁用下拉）；
   - OPC UA：锁定 `opc.tcp`（禁用下拉，**仅显示，`ToDevice` 时 dialect 置 null**——对齐 web `dialect=null`，后端 `ProtocolIdentifier.OpcUa` 无方言）。
3. **端点联动**（切协议时，仅当当前端点命中别的协议默认值时替换，保留用户自定义端点）：
   - Modbus → `127.0.0.1:502`；
   - S7 → `127.0.0.1:102`；
   - OPC UA → `opc.tcp://127.0.0.1:4840`。
4. **参数三路分流**（`ToDevice`）：Modbus 写 `UnitId/DataFormat(+RTU 串口参数)`；S7 写 `Rack/Slot/CpuType/PingAddress`；OPC UA 清空（不残留 Modbus/S7 参数）。
5. **校验增强**：
   - OPC UA 端点必须以 `opc.tcp://` 开头；
   - S7 `Rack` ∈ [0,7]、`Slot` ∈ [0,31]；
   - 现有 Name/Endpoint/超时/重试规则保留。
6. **回填**（`FromDevice`）：OPC UA 时 dialect 显示 `opc.tcp`（不落库）；Rack/Slot 等参数天然不写回（无此参数）。

### 3.2 点位表单（`PointEditor.cs` + `PointEditorWindow.xaml`）

- `PointEditor` 增加 `ProtocolName`（由 `PointsViewModel` 从设备协议注入），地址提示 `AddressHint` 按协议：Modbus `如 40001`、S7 `如 DB1.DBD0`、OPC UA `如 ns=2;i=1001`（窗口 ToolTip 展示，对齐 web 占位）。
- 校验文案按协议给示例，避免 OPC UA 用户误填 Modbus 寄存器号。

### 3.3 点位列表 + 批量生成（`PointsViewModel.cs` + `PointsWindow.xaml`）

- 新增「⚙ 批量生成」按钮 + 模态对话框（`PointBatchEditor` + `PointBatchWindow`），字段对齐 web：名称模板（`AI_{###}`）、起始地址（**按设备协议给默认**：Modbus `40001` / S7 `DB1.DBD0` / OPC UA `ns=2;i=1001`）、数量、数据类型、权限 + 递增规则提示（OPC UA 仅 `i=` 数值标识可 +1）。
- 流程：`PointBatchService.Generate(deviceId, 模板, 起始, 数量, 类型, 权限, 设备协议)` → `IPointManager.ImportAsync` → 逐条入 outbox（对齐 CSV 导入语义）→ 刷新列表。起始地址非法（如 OPC UA `s=` 标识）捕获 `ArgumentException` 内联提示，不落库。
- `PointsViewModel` / `PointsViewModelFactory` / `DeviceDialogService.ShowPoints` 增加 `protocolName` 参数（设备列表 `DeviceItem.Protocol` 取协议名透传）。

## 4. 实施步骤

### S1 设备表单协议感知（`DeviceEditor.cs`）
- `_protocolName` 通知追加 `IsOpcUa` / `IsDialectEditable` / `DialectItems` / `EndpointLabel`；
- `OnProtocolNameChanged`：切协议时联动 Dialect（S7→TCP、OPC UA→opc.tcp）与默认端点；
- `ToDevice`：OPC UA → `Dialect=null` + 参数清空；`FromDevice`：OPC UA dialect 回填 `opc.tcp`；
- `Validate`：新增 OPC UA 端点前缀 / S7 Rack / Slot 范围。

### S2 设备表单视图（`DeviceEditorWindow.xaml`）
- 协议下拉加 `OPC UA`；
- 传输方式 ComboBox：`ItemsSource="{Binding DialectItems}"` + `IsEnabled="{Binding IsDialectEditable}"` + `SelectedItem="{Binding Dialect}"`；
- 端点标签/TextBox ToolTip 绑定 `EndpointLabel`。

### S3 点位表单协议感知（`PointEditor.cs` + `PointEditorWindow.xaml`）
- 加 `ProtocolName` / `AddressHint`；地址校验文案按协议；窗口地址框 ToolTip 绑 `AddressHint`。

### S4 点位批量生成（`PointBatchEditor` + `PointBatchWindow` + `IDeviceDialogService.EditPointBatch` + `PointsViewModel.GenerateAsync` + `PointsWindow` 按钮）
- 新增 `PointBatchEditor`（ObservableObject，含协议感知默认起始地址 + 预览名 + 递增提示）；
- `PointsViewModel` 加 `GenerateBatchAsync`，复用 `PointBatchService.Generate` + `ImportAsync` + outbox；
- 协议透传链：`DevicesViewModel.ManagePoints` → `ShowPoints(deviceId, name, protocol)` → `PointsViewModelFactory.Create` → `PointsViewModel.ProtocolName` → 点位表单 / 批量生成。

### S5 测试
- `DeviceEditorTests`：OPC UA ToDevice（dialect=null + 无参数）、切协议端点/传输联动、OPC UA 端点校验、S7 Rack/Slot 范围、FromDevice OPC UA 往返；
- `PointEditorTests`：`AddressHint` 按协议、校验文案含 OPC UA 示例；
- `PointsViewModelTests`：批量生成成功（生成→导入→outbox→刷新）、取消不生成、非法起始地址报错不落库；既有 CreateVm/冒烟测试补协议参数。

## 5. 验证

- `dotnet build NitroGateway.slnx` 0 错误；
- `dotnet test tests/NitroGateway.UnitTests`（基线 705 全绿 + 新增用例）；
- 手工冒烟（可选）：桌面端建 S7（选协议自动带 Rack/Slot/CpuType/PingAddress、传输锁定 TCP）、建 OPC UA（端点默认 `opc.tcp://127.0.0.1:4840`、传输锁定 opc.tcp、无协议参数）、点位批量生成起始地址按协议给默认。

## 6. 风险与取舍

| 风险/取舍 | 说明 |
|---|---|
| 内部接口签名变更 | `IPointsViewModelFactory.Create` / `IDeviceDialogService` 增加协议参数，属桌面内部接口（非 Storage/Protocol.Abstraction），调用点全量同步，编译期兜底 |
| S7 传输锁定为行为收紧 | 有 S7 且 dialect 被误存为 RTU 的旧数据，`FromDevice` 归一化为 TCP 后保存即修复（对齐 web ADR-024 P3-2 语义） |
| 批量生成数量上限 | 复用 `PointBatchService` 5000 安全上限，不对接 `Count<=5000` 之外的风险 |
| 不实现 Browse 节点树 | 桌面 OPC UA 点位仍手填 NodeId（与 web 当前一致）；Browse 点选属 ADR-060 待办，不在本轮 |
| 雷区 | 不触碰工作区未提交的 ADR-060/061 改动；不动 `Storage/`、`Protocol/Abstraction/`；不升级依赖 |
