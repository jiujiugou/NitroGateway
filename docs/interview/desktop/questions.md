# Desktop 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 10 组 64 题；参考答案见 `answers.md`。

---

## 一、架构与定位

**Q1.1 ★** 桌面端（NitroGateway.Desktop）与 web 端是什么关系？「同一套引擎的两个壳」怎么理解？各自形态与运行环境差异？
代码定位：`GatewayHost.cs`（组合根）；`docs/11`、ADR-054/055。

**Q1.2 ★** `GatewayHost.Create` 的模块注册顺序为什么必须与 Webapi 一致（Forwarder 先于 Collection）？这个顺序决定了什么？
代码定位：`GatewayHost.cs`；`docs/11`「宿主架构」段。

**Q1.3 ★** 桌面端为什么不注册 Security（JWT/RBAC）也不起 Kestrel/Webapi？唯一的外部网络依赖是什么？
代码定位：`NitroGateway.Desktop.csproj`、`DesktopServiceCollectionExtensions.cs`。

**Q1.4 ★★** `AddNitroDesktopShell` 注册了哪些桌面壳服务？哪些是 Singleton、为什么 EventBridge 同时映射三个 listener 接口？
代码定位：`DesktopServiceCollectionExtensions.cs`。

**Q1.5 ★★** 桌面端 MQTT 上行与 web 中心形态有什么本质差异？topic 契约长什么样？「不面向中心/Ingest」意味着断网数据靠什么保底？
代码定位：`Forwarder.cs`、`MqttAlarmNotifier.cs`（topic 拼接）；`docs/11`「MQTT 数据上行」段。

---

## 二、启动与生命周期（App / GatewayHost）

**Q2.1 ★** 单实例是怎么实现的？命名 Mutex 解决什么问题（为什么必须单实例）？
代码定位：`App.xaml.cs`（`SingleInstanceMutexName`、`_ownsMutex`）。

**Q2.2 ★** 为什么启动时先显示 `StartupWindow` 反馈窗再切主窗口？启动失败怎么呈现？
代码定位：`App.xaml.cs`（OnStartup）；`ViewModels/StartupViewModel.cs`。

**Q2.3 ★★** 全局异常兜底（D7）覆盖哪两条通道？`DispatcherUnhandledException` 里 `e.Handled = true` 的意义？`OnExit` 为什么还要再调 StopAsync？
代码定位：`App.xaml.cs`（OnUnhandledException / OnDispatcherUnhandledException / OnExit）。

**Q2.4 ★★** `GatewayHost.StartAsync` 的三步顺序是什么？为什么必须在 `_host.StartAsync` 之前跑迁移和初始化 MQTT 转发开关？
代码定位：`GatewayHost.cs`（StartAsync）。

**Q2.5 ★★★** 优雅关闭（drain）到底排空什么？「未 flush 的数据留缓冲，下次启动续传」依赖哪两个机制？
代码定位：`GatewayHost.StopAsync`；`docs/11`「关闭」行；Forwarder 两阶段提交 + forward_buffer。

**Q2.6 ★★** `DisposeAsync` 为什么要把 `IHost` 按 `IAsyncDisposable` 解包？直接 `_host.DisposeAsync()` 会怎样？
代码定位：`GatewayHost.cs`（DisposeAsync）。

---

## 三、路径与配置（DesktopPathConfig）

**Q3.1 ★** 数据与日志缺省落在哪？覆盖优先级是怎样的（环境变量 / 设置页自定义 / 默认）？
代码定位：`Hosting/DesktopPathConfig.cs`（Apply）；`docs/11`「数据/日志」行。

**Q3.2 ★★** `FileSinkPathKey` 为什么按 Name 匹配 File sink 而不是硬编码数组索引？`ReadLogPathEnv` 为什么同时兼容索引 0 和 1？
代码定位：`DesktopPathConfig.cs`（FileSinkPathKey / ReadLogPathEnv）；ADR-027 P3-3/P3-5。

**Q3.3 ★★** 自定义日志目录（设置页保存）何时生效？为什么说「重启后生效」？校验失败/损坏时如何回退不阻断启动？
代码定位：`DesktopPathConfig.cs`（TryPrepareLogDirectory）；`SettingsViewModel.SaveLogDirectory`。

**Q3.4 ★★** `GatewayHost.Create` 为什么把 `ContentRootPath` 固定为 `AppContext.BaseDirectory`？不固定会有什么问题？
代码定位：`GatewayHost.cs`（Create）。

---

## 四、事件桥与 UI 线程模型（EventBridge / UiDispatcher）

**Q4.1 ★** EventBridge 同时实现 `IPointStoredSink` / `IDeviceHealthListener` / `IMqttStateListener` 三个接口，靠什么机制被自动接入？
代码定位：`EventBridge.cs` 类声明；`DesktopServiceCollectionExtensions.cs` 映射；模块注册机制（IPointStoredSink 等既有约定）。

**Q4.2 ★★** 为什么采用「200ms 合并成一帧 UiFrame」而不是每事件直接切 Dispatcher？`UiFrame.IsEmpty` 的用途？
代码定位：`EventBridge.cs`（DefaultFrameInterval、Flush、IsEmpty）。

**Q4.3 ★★** `UiFrame` 各字段的携带语义：Measurements / HealthChanges 按帧清空，MqttState 与 BufferBacklog 为什么「设置后每帧携带」？
代码定位：`EventBridge.cs`（UiFrame record + 注释）。

**Q4.4 ★★** 缓冲水位（backlog）是怎么轮询的？`_backlogDirty` 标志解决什么问题？
代码定位：`EventBridge.cs`（BacklogPollFrames / RefreshBacklogAsync / Flush）。

**Q4.5 ★★★** 帧循环异常后为什么不是直接退出而是自愈重启？`PeriodicTimer` + 200ms 延迟 + 记 Error 的三层设计好在哪？
代码定位：`EventBridge.cs`（LoopAsync、RestartDelay）；ADR-028 P3-1。

**Q4.6 ★★** `UiDispatcher.Post` 什么时候同步执行、什么时候入队？`TryBeginInvoke` 为什么吞掉异常？
代码定位：`Services/Infrastructure/UiDispatcher.cs`；ADR-027 P3-2。

**Q4.7 ★★** 事件收集用 `lock (_gate)`、帧发布用 try/catch——为什么收集不加 try/catch？帧发布异常的影响面？
代码定位：`EventBridge.cs`（OnStoredAsync / OnHealthChangedAsync / Flush）。

---

## 五、实时页性能（RealtimeViewModel）

**Q5.1 ★** 实时页分几层数据缓冲？每层解决什么问题（原始/显示/网格/内存最新值）？
代码定位：`RealtimeViewModel.cs` 类注释（ADR-045/050/051）。

**Q5.2 ★★** 原始缓冲为什么是普通 `List`（无集合通知）？`MaxChartPoints=7200` 怎么来的？溢出裁剪用 `RemoveRange` 的意义？
代码定位：`RealtimeViewModel.cs`（_rawValues、MaxChartPoints、OnFrame 溢出裁剪）。

**Q5.3 ★★★** `DownsampleMinMax` 的分桶降采样算法：保什么形状？为什么输出 ≤ `ChartWindowPoints`？「最新点恒在右边缘」怎么保证？
代码定位：`RealtimeViewModel.cs`（DownsampleMinMax、RefreshChart）。

**Q5.4 ★★** 500ms 图表刷新节流 + 500ms 表格刷新节流各在哪触发？`GridRefreshInterval` 为什么设计成 internal 可改？
代码定位：`RealtimeViewModel.cs`（ChartRefreshInterval、GridRefreshInterval、OnFrame）。

**Q5.5 ★★** `IsActive`（页面可见性）影响哪些行为？最小化/切页时为什么「摘除曲线数据」？
代码定位：`RealtimeViewModel.cs`（IsActive、OnIsActiveChanged）；`MainViewModel.SetRealtimeVisible`。

**Q5.6 ★★★** `_latestByPoint` 内存最新值缓存（ADR-050）解决了什么旧问题？「以帧为准、不覆盖」的边界是什么？
代码定位：`RealtimeViewModel.cs`（_latestByPoint、LoadPointsAsync 步骤 ①②）。

**Q5.7 ★★★** ADR-047：为什么 `Task.Run` 包 DB 查询？「SQLite async 是同步外包」是什么意思？
代码定位：`RealtimeViewModel.cs`（LoadPointHistoryAsync / LoadPointsAsync 注释）；HistoryViewModel 同款。

**Q5.8 ★★** `_loadVersion` 版本号机制防什么？为什么「过期结果直接丢弃」而不是合并？
代码定位：`RealtimeViewModel.cs`（_loadVersion、LoadPointsAsync / LoadPointHistoryAsync 回调校验）；`HistoryViewModel` 同款。

**Q5.9 ★★** `ApplyDeviceDiff`（ADR-048）对设备下拉做什么增量对账？为什么不能整表 Clear+重建？
代码定位：`RealtimeViewModel.cs`（ApplyDeviceDiff）。

**Q5.10 ★★** `RingObservableCollection.Replace / TrimFront` 与 ObservableCollection 原生行为差在哪？为什么只发一次 Reset？
代码定位：`ViewModels/Common.cs`；ADR-037 S12、ADR-045 P2。

---

## 六、MVVM 与表单（DeviceEditor / PointEditor / PointBatchEditor）

**Q6.1 ★** DeviceEditor 协议感知三路分流（docs/13）分哪三路？传输方式下拉为什么只有 Modbus 可编辑？
代码定位：`ViewModels/DeviceEditor.cs`（IsDialectEditable、DialectItems、ToDevice）。

**Q6.2 ★★** `OnProtocolNameChanged` 切协议联动哪些字段？「命中其他协议默认值才替换、保留用户自定义端点」这条规则为什么重要？
代码定位：`DeviceEditor.cs`（OnProtocolNameChanged）。

**Q6.3 ★★★** OPC UA 的 dialect 为什么「仅 UI 显示、ToDevice 时置 null」？S7 的 Rack/Slot 不落库会怎样？
代码定位：`DeviceEditor.cs`（ToDevice / FromDevice）；ADR-024 P3-1、docs/13。

**Q6.4 ★★** `Validate()` 的字段级错误用 `INotifyDataErrorInfo`，为什么字段变更即全量重算？错误字典与 `ErrorsChanged` 如何配合？
代码定位：`DeviceEditor.cs`（Validate / SetError / OnXxxChanged）。

**Q6.5 ★★** `FromDevice` 里的 `Normalize` 归一化解决什么历史脏数据问题？「ComboBoxItem: 」前缀哪来的？
代码定位：`DeviceEditor.cs`（FromDevice / Normalize）；ADR-036 绑定修复。

**Q6.6 ★** `PointEditor.AddressHint` 按协议给什么示例？为什么点位校验「地址非空」但**不**做协议级格式校验？
代码定位：`ViewModels/PointEditor.cs`（AddressHint / Validate）；`IPointManager.ValidateAsync` 注释。

**Q6.7 ★★** `PointBatchEditor` 的 `DefaultStartAddress` / `GenHint` / `PreviewName` 分别怎么算？OPC UA 批量生成只支持什么标识符？
代码定位：`ViewModels/PointBatchEditor.cs`；`PointBatchService.Generate`（协议递增语义）。

**Q6.8 ★★** `IUiTimer` 抽象的价值？为什么「轮询节奏是 view 关注点」？测试怎么受益？
代码定位：`Services/Infrastructure/IUiTimer.cs`、`DispatcherUiTimer.cs`；`DevicesViewModel` 构造注入。

**Q6.9 ★★** `PointsViewModelFactory` 为什么用 `IServiceScopeFactory` 建 scope 解析依赖？注释里提醒的「若未来改 Scoped 需上提生命周期」指什么？
代码定位：`Services/Infrastructure/PointsViewModelFactory.cs`；ADR-029 P2。

---

## 七、页面 ViewModel（设备/点位/告警/历史/设置）

**Q7.1 ★★** `DevicesViewModel.RefreshAsync` 为什么先 diff 再原位更新（ADR-037 S7）而不是 Clear+重建？这保住什么用户体验？
代码定位：`DevicesViewModel.cs`（RefreshAsync、ApplySnapshot）；`DeviceItem` 可观察属性。

**Q7.2 ★★** 设备列表的刷新节奏有几条？`DeviceCountChanged` 事件（ADR-037 S11）为什么复用 DevicesViewModel 而不是 MainViewModel 再查一次？
代码定位：`DevicesViewModel.cs`（_timer、OnFrame、DeviceCountChanged）；`MainViewModel`。

**Q7.3 ★★** 设备/点位增删改成功后除了写库还做了什么（outbox）？outbox 写入失败为什么「不阻断 UI」？
代码定位：`DevicesViewModel.cs`（RecordOutboxAsync）；`PointsViewModel.cs`（RecordOutboxAsync）；ADR-033 阶段 4。

**Q7.4 ★★** 点位 CSV 导入与批量生成成功后的 outbox 语义为什么是「逐条 RecordPointAsync」？与 Add/Edit 一致意味着什么？
代码定位：`PointsViewModel.cs`（ImportCsvAsync / GenerateBatchAsync）。

**Q7.5 ★★** `HistoryViewModel` 的分页怎么做的（PageSize / offset）？日期窗口为什么是 `[FromDate, ToDate 次日)`？
代码定位：`HistoryViewModel.cs`（QueryPageAsync）。

**Q7.6 ★** 告警页与告警规则页的能力边界：为什么桌面告警「只读浏览、无 ack」而 web 有 `POST /alarms/{id}/ack`？
代码定位：`ViewModels/AlarmsViewModel.cs`、`AlarmRulesViewModel.cs`；docs/11 能力矩阵。

---

## 八、配置同步（Sync）

**Q8.1 ★** 「从中心导入」与「配置自动同步」是什么关系？各自触发方式与数据方向？
代码定位：`SettingsViewModel.ImportFromCenterAsync`；`Services/Sync/SiteConfigSyncService.cs`；ADR-033 阶段 2/3/4。

**Q8.2 ★★** `CenterConfigImporter.ImportAsync` 以中心为准重置本地的三步是什么？为什么每台设备用独立 scope？
代码定位：`Services/Sync/CenterConfigImporter.cs`（ImportAsync）；ADR-029（Scoped 管理服务）。

**Q8.3 ★★★** outbox 的固定主键设计（`d|{deviceId}` / `p|{deviceId}|{pointId}`）解决了什么问题？「删除后又重建原地替换行类型」的语义是什么？
代码定位：`Services/Sync/ConfigSyncOutboxStore.cs`（Key / UpsertAsync）；ADR-033 阶段 4。

**Q8.4 ★★★** `SiteConfigSyncService.SyncOnceAsync` 的单轮流程？`ApplySnapshotAsync` 双向 UpdatedAt 合并的四种分支各是什么？
代码定位：`SiteConfigSyncService.cs`（SyncOnceAsync / ApplySnapshotAsync）；ADR-033 阶段 3。

**Q8.5 ★★★** `PushPendingAsync` 为什么按设备聚合为「每台一条变更」？设备删除（tombstone）为什么优先于点位行？上报成功后才清 outbox 的含义？
代码定位：`SiteConfigSyncService.cs`（PushPendingAsync）。

**Q8.6 ★★★** 中心 Token 的安全存储链路：DPAPI 为什么用 CurrentUser 作用域？「明文只保留内存」怎么做到？旧版明文怎么迁移？
代码定位：`Services/Sync/CenterSyncSettingsStore.cs`；`Services/Settings/DpapiProtector.cs`；ADR-037 S5。

**Q8.7 ★★** `SiteIdProvider` 的生效 siteId 解析顺序？「default」为什么视为未初始化？重新生成后为什么「重启后生效」？
代码定位：`Services/Sync/SiteIdProvider.cs`；`Shared/SiteOptions.cs`；ADR-036。

**Q8.8 ★★** `CenterConfigClient` 为什么不用 Transport 的 HTTP 客户端而独立用 HttpClient？超时设多少、在哪配置？
代码定位：`Services/Sync/CenterConfigClient.cs`；`DesktopServiceCollectionExtensions.cs`（HttpClient Singleton）。

---

## 九、设置与安全 / MQTT 开关 / 连接测试

**Q9.1 ★** `SettingsViewModel` 展示了哪些「现场可视性」信息？哪些只读、哪些可编辑？
代码定位：`ViewModels/SettingsViewModel.cs` 构造 + 属性。

**Q9.2 ★★** `DesktopForwardMqttToggle`（ADR-059）为什么用 `Volatile.Read/Write` 做内存缓存？`SetEnabledAsync` 的「加载→改字段→保存」合并写防什么？
代码定位：`Services/Settings/DesktopForwardMqttToggle.cs`；ADR-059/061。

**Q9.3 ★★** ADR-061：为什么只在「实际值变化」时才触发 `EnabledChanged`？不这样做会怎样（结合 MQTT 断开/重连）？
代码定位：`DesktopForwardMqttToggle.cs`（SetEnabledAsync）；`MqttHostedService` 对状态 Disabled 的处理。

**Q9.4 ★★** `DeviceConnectionTester` 为什么复用 `IProtocolDriverFactory`？为什么「连接成功不等于测试通过」，必须再 Ping？
代码定位：`Services/Connectivity/DeviceConnectionTester.cs`；ADR-023/044。

---

## 十、开放题 / 与 web 差异 / 演进

**Q10.1 ★★** 桌面与 web 的能力差距（docs/11 核验后）：桌面缺什么、独有什么？为什么这些差距存在（架构/形态原因）？
代码定位：docs/11 能力矩阵；web 侧代码（历史曲线 ECharts、串口枚举 SystemStatus）。（注：web 死信管理页 2026-08-22 已随转发简化删除）

**Q10.2 ★★★** 桌面端「历史曲线缺失但 web 有」：如果要在桌面补上，RealtimeViewModel 的哪套机制可以复用？还缺什么？
代码定位：`RealtimeViewModel`（降采样/节流/环形缓冲）；`HistoryViewModel`（分页查询）。

**Q10.3 ★★★** 列出 Desktop 模块至少 3 个可改进点并说明理由（可从性能、异常、安全、同步一致性角度想）。
代码定位：全模块。

**Q10.4 ★★★** `SiteConfigSyncService` 与「手动导入」两种模式并存，手动导入后清空 outbox 的语义是什么？会不会丢现场未上报改动？怎么权衡？
代码定位：`SettingsViewModel.ImportFromCenterAsync`（确认对话框）；`CenterConfigImporter`；ADR-033 阶段 2 vs 3/4。

**Q10.5 ★★★** 不看代码画出三条时序：① 启动到 UI（App → GatewayHost → 迁移 → 后台服务 → EventBridge 帧 → UiDispatcher → 各页）；② 断网现场改设备 → outbox → 联网 → SiteConfigSyncService 上报 → 中心裁决 → 回写；③ 实时页一帧数据从 SQLite 存储事件到曲线/网格的完整链路（含 200ms 帧、IsActive、500ms 节流、降采样）。
代码定位：全模块 + 对应测试。

---

## 十一、一页速记（答完自检）

- 桌面 = WPF 壳 + 全模块进程内（GatewayHost 组合根）；无 Kestrel/Security；唯一外部依赖是中心导入的 HttpClient
- 注册顺序 = 关闭顺序：Forwarder 先于 Collection → 先停采集、再排空转发缓冲；未 flush 数据留 forward_buffer 续传
- 单实例 Mutex 防双写同一 SQLite；全局异常兜底非阻塞提示不闪退
- 路径优先级：环境变量 > 设置页自定义日志目录（desktop-settings.json）> `%LocalAppData%\NitroGateway`
- EventBridge：200ms 帧合并 + 2s 水位轮询 + 自愈重启；UiDispatcher 关闭期吞异常
- 实时页四层缓冲：原始 List（7200 无通知）→ min/max 降采样（≤1000）→ 500ms 图表节流 + 500ms 表格节流 → 内存最新值（切设备即时填充）
- 版本号 _loadVersion 防过期结果；Task.Run 移 SQLite 查询出 UI 线程
- 表单协议感知：Modbus TCP/RTU、S7 锁 TCP、OPC UA 锁 opc.tcp（dialect=null）；Rack 0-7 / Slot 0-31；OPC UA 端点须 opc.tcp://
- 现场改动必入 outbox（固定主键原地替换）；同步：拉快照双向合并 + 聚合上报 + 成功才清行；未配中心静默跳过
- Token：DPAPI（CurrentUser）加密落盘，明文不落盘；SiteId 解析：配置 > site.json > 自动生成
- MQTT 转发开关：desktop-settings.json 持久化、Volatile 内存缓存、实际变化才发事件；关闭仅暂停上云，本地照常
