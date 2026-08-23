# ADR-029: 桌面端本地设备/点位配置界面（B 方案阶段 1）

- 日期: 2026-08-10 | 状态: 已实施（2026-08-10，阶段 1；阶段 2 仅记录决策） | 来源: B 方案闭环缺口 B3——桌面端要采集，但设备/点位配置无任何入口（DevicesViewModel 只读、SettingsViewModel 注释「配置编辑属 P2」）
- 范围: 仅 src/NitroGateway.Desktop（ViewModels + Views + DialogService）+ 桌面测试；不动 Webapi/契约/数据模型

## 三问
- 为什么做: 桌面端没有设备/点位配置入口，现场只能手改 SQLite，B 方案无法落地
- 验收: 桌面端离线完成「新增设备 → 配点位 → 开始采集 → 实时页出数」；错误路径有提示不崩溃
- 不做会怎样: B3 缺口持续存在，桌面端是半成品

## 设计
- P1 设备 CRUD: DevicesViewModel 增加 Add/Edit/Delete 命令 + SelectedDevice；复用 IDeviceManager（RegisterAsync upsert / UnregisterAsync），零后端改动
- P2 点位管理: 设备行「点位管理」打开 PointsWindow（独立 PointsViewModel），复用 IPointManager（AddAsync/UpdateAsync/RemoveAsync/GetByDeviceAsync）
- P3 表单: DeviceEditor/PointEditor 可变表单模型（ObservableObject，协议联动显示 Modbus TCP/RTU/S7 字段：UnitId/DataFormat/BaudRate/Parity、Rack/Slot/CpuType/PingAddress、超时/重试）；字段与 Web DeviceForm.vue 对齐（ADR-024 P3-1/P3-2 同款）
- P4 对话框抽象: IDeviceDialogService（EditDevice/EditPoint/Confirm/ShowPoints），WPF 实现 DeviceDialogService（模态 Window）；ViewModel 依赖接口，测试用 fake
- P5 阶段 2 决策（暂不做，仅记录）: 中心配置导入——中心加只读导出 API，桌面端「从中心导入」全量覆盖本地（中心为准），Token 存 %LocalAppData%，手动触发

## 关键决策
- 只做阶段 1（用户拍板 2026-08-10）；阶段 2 冲突策略「中心为准全量覆盖」已确认，作为后续设计输入
- 复用现有 IDeviceManager/IPointManager（桌面端已注册 AddNitroDevice），不新增后端接口、不改数据模型
- 设备状态新建默认 Unknown，由 HealthMonitor 驱动（不手填 Online 伪状态）

## 验证（2026-08-10 已完成）
- 新增 DevicesViewModelTests(8) + PointsViewModelTests(7) + DeviceEditorTests(5) + PointEditorTests(3) + DesktopViewSmokeTests 窗口冒烟(1)——fake dialog/manager，覆盖新增/编辑/删除/点位管理、取消不落库、RTU/S7 参数映射、往返回填
- 收尾: build 0 错误；UnitTests 414（+24）+ IntegrationTests 43 全绿；STA 冒烟实例化 DeviceEditorWindow/PointEditorWindow/PointsWindow 无异常

## 风险
- WPF 对话框绑定需 DeviceEditor 为 ObservableObject 才能在协议切换时联动显隐字段（非 POCO）
- IDeviceManager/IPointManager 是 Scoped：ViewModel 命令内用 IServiceScopeFactory 建作用域解析（与 AlarmsViewModel 同法）
