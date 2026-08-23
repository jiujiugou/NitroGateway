# 桌面端（NitroGateway.Desktop / WPF）功能清单与架构

> 生成时间: 2026-08-18 | 基于代码核验（src/NitroGateway.Desktop）
> 关联: ADR-026（壳设计）/ 027（代码走查）/ 029（设备点位 UI）/ 033（中心配置同步）/ 036（站点标识）/
> 037/039/040/041（UI 打磨）/ 043（告警规则）/ 044（连接测试）/ 045/046/047/048/050/051（实时页性能）/
> 052（试运行审计）/ 054-055（web vs 桌面定位）

## 一句话

Windows 现场采集端（WPF，`net10.0-windows`）：自包含组合根 `GatewayHost` 全模块进程内，
与 web（Linux 边缘）是**同一套引擎的两个壳**——复用 Collection / Forwarder / Alarm / Device /
Persistence / Protocol / Transport 类库，本机采集 → 本机 SQLite → MQTT 上报，另带现场 UI。

## 运行方式与数据路径

| 项 | 说明 | 位置 |
| --- | --- | --- |
| 启动 | 命名 Mutex 单实例（防双写同一 SQLite）→ 启动迁移 → 后台服务 → 主窗口；启动中先显示反馈窗（迁移+服务启动可能数秒） | `App.xaml.cs`、`Hosting/GatewayHost.cs`、`Views/StartupWindow.xaml` |
| 数据/日志 | 缺省落 `%LocalAppData%\NitroGateway`（SQLite + `logs/`，日轮转保留 7 天）；环境变量优先，设置页可自定义日志目录 | `Hosting/DesktopPathConfig.cs`、`appsettings.json` |
| 关闭 | 优雅 drain：按注册逆序停（先采集 → 排空 forward_buffer → MQTT）；未 flush 数据留缓冲，下次启动续传 | `GatewayHost.StopAsync` |
| 免登录 | 本机会话免登录（无 AddNitroSecurity、无 Kestrel/Webapi 引用）；唯一外部依赖为可选 HttpClient（中心配置导入） | `NitroGateway.Desktop.csproj`、`DesktopServiceCollectionExtensions.cs` |

## 宿主架构（GatewayHost）

模块注册顺序与 Webapi 一致（Forwarder 先于 Collection，保证关闭时先停采集、再排空转发缓冲）：

```
AddNitroGatewayHost → AddNitroSqlite → AddNitroDevice → AddNitroProtocol
  → AddNitroAlarm → AddNitroForwarder → AddNitroCollection → AddNitroMqtt
  → AddNitroDesktopShell（EventBridge / UiDispatcher / ViewModels / 对话框服务）
```

启动迁移复用中心同 schema 的 FluentMigrator `M001~` 迁移；`EventBridge` 同时实现
`IPointStoredSink` / `IDeviceHealthListener` / `IMqttStateListener`，由既有模块注册机制自动接入，
200ms 帧节流（采集/健康/MQTT 事件 → UiFrame）→ `UiDispatcher` 贴 UI 线程。

## MQTT 数据上行（独立发布，不依赖中心）

桌面端 MQTT 是**独立旁路**：只按配置的 broker 发布，**不面向中心/Ingest**——谁订阅谁消费。

| 项 | 说明 |
| --- | --- |
| Broker | 默认 `localhost:1883`（本机/现场 broker）；远端用环境变量 `MQTT__Host` / `MQTT__Port` 覆盖 |
| 遥测 topic | `nitrogateway/{siteId}/{deviceId}/measurements`，QoS 1（`Forwarder.cs`） |
| 告警 topic | `nitrogateway/{siteId}/{deviceId}/alarms`，QoS 1（`MqttAlarmNotifier.cs`） |
| 消费方 | 任意 MQTT 订阅端订阅对应 topic 即可（现场调试工具、云端服务、自建 Ingest 等）；桌面端自身不订阅不消费（本地展示走 SQLite + EventBridge） |
| 断网不丢 | `forward_buffer` 排队重传（两阶段提交，失败批次重试，超限丢弃）；broker 重连自动重订阅（1s→30s 退避，上限 10 次） |
| 与配置同步 | 遥测走 MQTT，配置同步走 HTTP 中心 Webapi，两条通道互不相干 |

即：桌面端只负责"发布到 broker"，topic 按 `siteId/deviceId` 分层；没有中心时消息在 broker 上自然晾着，
不影响本地采集/落库/展示（本地 SQLite 才是桌面端自己的可靠存储）。

## 页面与功能清单（按对比表逐项核验）

| 能力 | 桌面状态 | 代码位置 | 说明 |
| --- | --- | --- | --- |
| 设备 CRUD | ✓ | `ViewModels/DevicesViewModel.cs` + `DeviceEditor.cs` + `Views/DeviceEditorWindow.xaml` | 增/改/删经 `IDeviceManager`（按 Id upsert，删设备级联删点位）；列表 5s 自动刷新 + 健康变更帧即时刷新，含在线/离线/点位统计卡 |
| 点位 CRUD | ✓ | `ViewModels/PointsViewModel.cs` + `PointEditor.cs` + `Views/PointsWindow.xaml` | 模态窗口增/改/删经 `IPointManager`；改动入 outbox（ADR-033 阶段 4） |
| 点位 CSV 导出 | ✓ | `PointsViewModel.ExportCsvAsync` + `Services/CsvFileService.cs` + `PointBatchService.ExportCsv` | 保存对话框（Microsoft.Win32） |
| 点位 CSV 导入 | ✓ | `PointsViewModel.ImportCsvAsync` + `CsvFileService` + `PointBatchService.ParseCsv` | 打开 CSV → 解析 → `ImportAsync` → 逐条入 outbox；PointsWindow「⬆ 导入 CSV」 |
| 点位批量生成 | ✓ | `PointsViewModel.GenerateBatchAsync` + `PointBatchEditor` + `Views/PointBatchWindow.xaml` | 模态对话框（名称模板 + 起始地址按协议默认 + 递增规则提示）→ `PointBatchService.Generate`（Modbus 寄存器步长 / S7 DB 字节步长 / OPC UA 数值标识 +1）→ `ImportAsync` → 逐条入 outbox；PointsWindow「⚙ 批量生成」（docs/13） |
| 实时曲线 | ✓ | `Views/RealtimeView.xaml` + `ViewModels/RealtimeViewModel.cs` + `Services/RealtimeChartFactory.cs` | LiveCharts2：预载 2h（7200 点环形缓冲）+ min/max 分桶降采样到 1000 点 + 500ms 刷新节流 + 页面不可见/最小化暂停（ADR-045/050/051） |
| 历史曲线 | ✗ **（对比表标注 ✓，代码无）** | `Views/HistoryView.xaml` | 历史查询页为**分页表格**（时间/工程值/原始值/质量/错误），无图表；曲线只在实时页 |
| 历史 CSV 导出 | ✗ | — | 桌面与 web 两边都缺（不算单边差距） |
| 设备连接测试 | ✓ | `Services/DeviceConnectionTester.cs` + `DeviceEditor.TestConnectionAsync` | 与采集引擎共用同一协议驱动工厂；Connect+Ping 双验（ADR-023 防假阳性），不重试 |
| 串口枚举/状态 | ✗ **（对比表标注 ✓，代码无）** | `DeviceEditor.cs` | RTU 只支持手动填端点（如 "COM3"），**无串口枚举/占用状态查询**；web `SystemStatus` 页有（对比表注释中的 "System 页有" 指 web） |
| 告警查看 | ✓ | `ViewModels/AlarmsViewModel.cs` + `Views/AlarmsView.xaml` | 最近 24h（活跃置顶），5s 自动刷新，严重性着色；**只读浏览，无确认（ack）按钮**（web 有 `POST /alarms/{id}/ack`） |
| 告警规则管理 | ✓ | `ViewModels/AlarmRulesViewModel.cs` + `AlarmRuleEditor.cs` + `Views/AlarmRuleEditorWindow.xaml` | 全部规则（含禁用）列表 + 增/改/删模态对话框，经 `IAlarmRuleRepository` 落库（ADR-043） |
| 系统状态（熔断/健康/转发） | 部分 | `ViewModels/SettingsViewModel.cs` + `DevicesViewModel.cs` | 设置页展示 MQTT 连接状态/缓冲积压/库与日志路径/采集与转发间隔（只读）；设备页展示每台健康状态；**无熔断状态、无转发明细**——web 反超 |
| 死信管理 | ✗(已删) | — | 2026-08-22 转发改为重试超限即丢弃，web 死信管理(F-39/DeadLettersView)已删除，桌面无需对齐 |

## 能力矩阵（web vs 桌面，核验后）

| 能力 | 桌面(WPF) | Web | 结论（核验） |
| --- | --- | --- | --- |
| 设备/点位 CRUD | ✓ | ✓ | 对等 |
| 点位批量生成 | ✓ | ✓ | 对等（docs/13 已补齐桌面） |
| 点位 CSV 导出 | ✓ | ✓ | 对等 |
| 点位 CSV 导入 | ✓ | ✗ 后端有前端未接 | Web 缺 |
| 实时曲线 | ✓ LiveCharts2（2h+环形缓冲） | ✗ 仅数值卡片 | Web 缺 |
| 历史曲线 | ✗（仅表格） | ✓ ECharts step 线 | **Web 反超**（对比表误标桌面 ✓） |
| 历史 CSV 导出 | ✗ | ✗ | 两边都缺（不算单边差距） |
| 设备连接测试 | ✓ | ✓ | 对等 |
| 串口枚举/状态 | ✗（手动填 COM） | ✓ System 页有 | **Web 反超**（对比表误标桌面 ✓） |
| 告警/告警规则 | ✓（规则全、告警只读） | ✓（含 ack） | 基本对等，web 略多 ack |
| 系统状态（熔断/健康/转发） | 部分（设置页+设备页） | ✓ 更全 | Web 反超 |
| 死信管理 | ✗(已删) | ✗(已删) | 2026-08-22 转发简化：重试超限即丢弃，无死信管理 |

## 桌面独有能力（web 没有）

- **从中心导入**：设置页中心地址/Token（DPAPI 落盘 `%LocalAppData%\NitroGateway\center-sync.json`）→ 拉快照 → 确认覆盖 → 以中心为准重置本地设备/点位（ADR-033 阶段 2，F-52）
- **站点标识 SiteId**：自动生成/编辑/重新生成，随数据上行区分现场（ADR-036）
- **配置自动同步**：outbox（本地待上报队列）+ `SiteConfigSyncService` 周期同步中心（ADR-033 阶段 3/4；未配中心地址静默跳过）
- **状态栏**：MQTT 连接状态 / 转发缓冲积压 / 设备数（MainViewModel，EventBridge 帧驱动）
- **单实例 + 全局异常兜底**：命名 Mutex + AppDomain/Dispatcher 异常非阻塞提示不闪退（D6/D7）
- **现场可观测**：设置页直接可见 SQLite 路径、日志目录、采集/转发间隔（D9）

## 实时曲线实现要点（LiveCharts2）

- 预载 2h：`MaxChartPoints=7200`（1s 采集 ≈ 2h 窗口），环形缓冲普通 List 无集合通知（ADR-045 P2）
- 降采样：`DownsampleMinMax` 按时间分桶保留每桶 min/max（保尖峰/谷底），输出 ≤ `ChartWindowPoints=1000` 点，最新点恒在右边缘
- 刷新节流：最多每 500ms 重绘一次；页面不可见（切走/最小化）整帧丢弃并摘除曲线数据
- 表格节流：帧值先入内存缓存（O(1) 无通知），DataGrid 行最多每 500ms 批量刷一次，防 500 点位 × 4 属性 × 5fps 压满 UI 线程（ADR-051）

## 测试覆盖

`tests/NitroGateway.UnitTests`（基线 130 通过）中桌面相关：
`DesktopShellRegistrationTests`（DI 注册冒烟）、`DesktopViewSmokeTests`（视图解析）、
`DevicesViewModelTests` / `PointsViewModelTests` / `RealtimeViewModelTests`（含 Downsample/GridRefresh 节流）/
`HistoryViewModelTests` / `AlarmRulesViewModelTests` / `SettingsViewModelTests`。

## 与 web 的定位差异（ADR-054/055）

- 桌面端 = Windows 边缘（自包含组合根，`GatewayHost` 全模块进程内）；web = Linux 边缘（webapi Gateway 形态 + Vue）
- 两者完全独立各自运行、各自功能完整，不需要同时启动（同机同启会双写同一 SQLite + 双 MQTT 发布）
- 均以「完整独立边缘」为标准：桌面独缺 历史曲线 / 串口枚举 / 系统状态明细，
  独有 从中心导入 / 站点标识 / 配置自动同步 / 本机会话免登录
