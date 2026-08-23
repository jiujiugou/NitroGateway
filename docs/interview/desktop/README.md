# Desktop 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Desktop`（WPF 现场采集端：自包含组合根 `GatewayHost` 全模块进程内，与 web 是同一套引擎的两个壳）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/并发/失败路径）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
App（单实例/异常兜底）→ GatewayHost（组合根/注册顺序/迁移）
→ DesktopPathConfig（路径与配置优先级）→ 模块注册（AddNitroDesktopShell）
→ EventBridge / UiDispatcher（服务事件 → UI 帧）→ 各页 ViewModel（设备/实时/历史/告警/设置）
→ 表单模型（DeviceEditor / PointEditor / PointBatchEditor，协议感知）
→ 配置同步（outbox / SiteConfigSyncService / 中心导入 / DPAPI）→ 连接测试 → 与 web 差异 → 开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 应用入口 | `App.xaml.cs` | 命名 Mutex 单实例 + 全局异常兜底（D7）+ 启动反馈窗 + MainWindow |
| 宿主 | `Hosting/GatewayHost.cs` | 组合根：路径 → Serilog → 模块注册（顺序对齐 Webapi）→ 迁移 → 优雅关闭 |
| 路径配置 | `Hosting/DesktopPathConfig.cs` | SQLite/日志缺省 `%LocalAppData%\NitroGateway`；环境变量优先；自定义日志目录 |
| 事件桥 | `Messaging/EventBridge.cs` | 实现 IPointStoredSink/IDeviceHealthListener/IMqttStateListener，200ms 合并成 UiFrame |
| UI 调度 | `Services/Infrastructure/UiDispatcher.cs` | 后台帧 → UI 线程；关闭期丢弃（ADR-027 P3-2） |
| 轮询抽象 | `Services/Infrastructure/IUiTimer.cs`、`DispatcherUiTimer.cs` | 轮询节奏与 WPF 解耦，测试可注入替身 |
| 集合工具 | `ViewModels/Common.cs`（RingObservableCollection） | 批量移除/整份替换只发一次 Reset 通知（ADR-037 S12） |
| 图表工厂 | `Services/Infrastructure/RealtimeChartFactory.cs` | 曲线配色/坐标轴/labeler 从 ViewModel 剥离（ADR-045 P3） |
| 实时页 | `ViewModels/RealtimeViewModel.cs` | 帧驱动网格 + LiveCharts2 曲线；2h 原始缓冲 + min/max 降采样 + 500ms 节流（ADR-045/047/048/050/051） |
| 设备页 | `ViewModels/DevicesViewModel.cs`、`DeviceItem.cs` | 5s 轮询 + 健康帧即时刷新；diff 原位更新 + 统计卡（ADR-037 S7/S11） |
| 点位页 | `ViewModels/PointsViewModel.cs`、`PointItem.cs` | 点位 CRUD / CSV 导入导出 / 批量生成；改动入 outbox |
| 历史页 | `ViewModels/HistoryViewModel.cs` | 设备+点位+日期区间分页表格（QueryPagedAsync） |
| 告警/规则 | `ViewModels/AlarmsViewModel.cs`、`AlarmRulesViewModel.cs` | 最近 24h 告警只读；规则 CRUD（ADR-043） |
| 设置页 | `ViewModels/SettingsViewModel.cs` | 现场可视性 + 中心导入 + SiteId + 日志目录 + MQTT 开关（ADR-033/036/059） |
| 设备表单 | `ViewModels/DeviceEditor.cs` | 协议感知三路分流（Modbus/S7/OPC UA，docs/13）+ INotifyDataErrorInfo 校验 + 连接测试 |
| 点位表单 | `ViewModels/PointEditor.cs` | 地址提示按协议（40001 / DB1.DBD0 / ns=2;i=1001） |
| 批量生成表单 | `ViewModels/PointBatchEditor.cs` | 协议感知默认起始地址 + 递增规则提示 + 名称预览 |
| 对话框 | `Services/Dialogs/*.cs` | WPF 模态实现，ViewModel 依赖接口便于单测（ADR-029 P4/ADR-043） |
| 工厂 | `Services/Infrastructure/PointsViewModelFactory.cs` | scope 解析收敛（ADR-029 P2） |
| 连接测试 | `Services/Connectivity/DeviceConnectionTester.cs` | Connect+Ping 双验，复用协议驱动工厂（ADR-023/044） |
| 本地设置 | `Services/Settings/DesktopSettingsStore.cs`、`DesktopForwardMqttToggle.cs` | desktop-settings.json：日志目录 + MQTT 转发开关（ADR-059/061） |
| Token 加密 | `Services/Settings/DpapiProtector.cs`、`CenterSyncSettingsStore.cs` | DPAPI（CurrentUser）加密中心 Token，明文不落盘（ADR-037 S5） |
| 中心客户端 | `Services/Sync/CenterConfigClient.cs` | GET export / GET configsync/export / POST configsync/push |
| 中心导入 | `Services/Sync/CenterConfigImporter.cs` | 以中心为准重置本地设备/点位（ADR-033 阶段 2） |
| outbox | `Services/Sync/ConfigSyncOutboxStore.cs` | 现场改动待上报队列，固定主键原地替换行类型（ADR-033 阶段 4） |
| 自动同步 | `Services/Sync/SiteConfigSyncService.cs` | 周期拉快照双向合并 + 上报 outbox（阶段 3/4） |
| 站点标识 | `Services/Sync/SiteIdProvider.cs`、`SiteSettingsStore.cs` | site.json + 配置/存储/自动生成三级解析（ADR-036） |

## 跨模块依赖（答题时需要知道的上下文）

- `Host`（`AddNitroGatewayHost`）：生命周期与优雅关闭（`GatewayLifecycle` drain 语义，关闭顺序由注册顺序决定）
- `Persistence.Sqlite`：`AddNitroSqlite` + `MigrationRunner.Run`（复用中心同 schema 的 M001~ 迁移）；`SqlitePragmas.Apply`
- `Device`：`IDeviceManager` / `IPointManager` / `IDeviceSnapshotCache` / `IDeviceHealthMonitor` / `PointBatchService`
- `Protocol`：`IProtocolDriverFactory`（连接测试与采集同链路）
- `Forwarder` / `Storage.Buffer`：`IForwardBuffer`（水位轮询）、`IForwardMqttToggle`（转发总开关）
- `Transport.MQTT`：`MqttConnectionState` / `IMqttStateListener` / `MqttConnectionOptions`
- `Shared`：`OperationResult` / `OperationalError` / `SiteOptions`（siteId 校验与生成）
- 测试：`DesktopShellRegistrationTests` / `DesktopViewSmokeTests` / `DesktopPathConfigTests` / `DesktopSettingsStoreTests` / `CenterSyncSettingsStoreTests` / `DeviceEditorTests` / `PointEditorTests` / `PointBatchEditorTests` / `DevicesViewModelTests` / `PointsViewModelTests` / `RealtimeViewModelTests` / `HistoryViewModelTests` / `AlarmRulesViewModelTests` / `SettingsViewModelTests` / `StartupViewModelTests`

## 注意事项

- **代码是唯一事实来源**。docs/11 能力矩阵存在文档漂移（如历史曲线/串口枚举标注），答题以代码为准；docs/13 已实施并落地（`dotnet test` 基线已含新增用例）。
- 区分「代码强制」与「约定约束」：桌面接口（`IPointsViewModelFactory` / `IDeviceDialogService`）是桌面内部契约，可随调用点同步改；`Storage/`、`Protocol/Abstraction/` 纯接口只增不删。
- **单实例 Mutex 防双写同一 SQLite**：桌面与 web 同机同启会双写同一库 + 双 MQTT 发布，设计上两者独立运行（ADR-054/055）。
- 桌面端**无 Kestrel / 无 AddNitroSecurity**：本机会话免登录；唯一外部依赖是可选 HttpClient（中心配置导入）。
- MQTT 上行是**独立旁路**：只发布到 broker，topic `nitrogateway/{siteId}/{deviceId}/…`，不面向中心 Ingest；本地可靠存储是 SQLite。
- 答完所有题目后，试着不看代码画出三条时序：① 启动（App → GatewayHost → 迁移 → 服务 → 帧 → UI）；② 断网 → outbox 累积 → 联网 → 上报中心 → 双向合并回写；③ 实时页一帧数据从 SQLite 到曲线/网格（含节流与暂停）——能画出来就是吃透了。
