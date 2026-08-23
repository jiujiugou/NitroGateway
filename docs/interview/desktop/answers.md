# Desktop 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：docs/11 能力矩阵存在漂移（历史曲线/串口枚举标注），答题以代码 + XML 注释为准。

---

## 一、架构与定位

**Q1.1 两个壳、同一套引擎**
桌面端是 Windows 现场采集端（WPF，`net10.0-windows`），web 端是 Linux 边缘（webapi Gateway + Vue）。两者**复用同一批核心类库**（Collection / Forwarder / Alarm / Device / Persistence / Protocol / Transport），各自有独立组合根（桌面 `GatewayHost`、web `Program.cs`），可独立运行、功能各自完整。数据流一致：本机采集 → 本机 SQLite → MQTT 上报。差异在壳：桌面是 WPF UI + 本机会话免登录；web 是 REST/SignalR + Vue 面板 + JWT/RBAC。设计上**不要同机同启**——会双写同一 SQLite + 双 MQTT 发布（ADR-054/055）。

**Q1.2 注册顺序 = 关闭顺序**
`GatewayHost.Create` 顺序：`AddNitroGatewayHost → AddNitroSqlite → AddNitroDevice → AddNitroProtocol → AddNitroAlarm → AddNitroForwarder → AddNitroCollection → AddNitroMqtt → AddNitroDesktopShell`。`IHost.StopAsync` 按注册**逆序**停止：先停采集（`GatewayLifecycle` drain，最后一轮入缓冲）→ 转发排空 `forward_buffer` → MQTT。Forwarder 注册在 Collection 之前，保证关闭时「先停采集、再排空转发缓冲」——不然采集已停、缓冲里残留的数据来不及上云就断连了。

**Q1.3 免登录、无 Kestrel/Security**
桌面是单机现场工具：用户就在本机操作，不需要远程鉴权，所以 `NitroGateway.Desktop.csproj` 不引用 Webapi，也不调用 `AddNitroSecurity` / 不起 Kestrel。唯一外部网络依赖是**可选的** `HttpClient`（中心配置导入/同步，`DesktopServiceCollectionExtensions` 里 Singleton 注册，15s 超时）。无中心地址时同步服务静默跳过，本地采集/落库/展示完全自洽。

**Q1.4 AddNitroDesktopShell 注册**
注册：`UiDispatcher`（Singleton）、`EventBridge`（Singleton）+ 三个接口映射（`IPointStoredSink` / `IDeviceHealthListener` / `IMqttStateListener` 都指向同一 EventBridge 实例）、六个页面 ViewModel（Main/Devices/Realtime/Alarms/AlarmRules/History/Settings，Singleton）、对话框服务（IDeviceDialogService / ICsvFileService / IAlarmRuleDialogService）、`IPointsViewModelFactory`、`IDeviceConnectionTester`、`HttpClient`（Singleton 15s）、`ICenterSyncSettingsStore` / `IDesktopSettingsStore` / `IForwardMqttToggle` / `ISiteSettingsStore` / `ISiteIdProvider` / `ICenterConfigClient` / `ICenterConfigImporter` / `IConfigSyncOutboxStore`，以及 HostedService `SiteConfigSyncService`。
EventBridge 同时映射三个接口 = **一个对象同时消费采集/健康/MQTT 三类事件**，把它们合并成统一 UI 帧——这是「服务事件 → UI」的唯一入口，避免各模块各自 push 到 UI 造成线程混乱。

**Q1.5 MQTT 独立旁路**
桌面端 MQTT 只**发布**到配置的 broker，不面向中心 Ingest——谁订阅谁消费，现场调试工具/云端自建服务可订阅。topic：遥测 `nitrogateway/{siteId}/{deviceId}/measurements`（QoS 1，Forwarder）、告警 `nitrogateway/{siteId}/{deviceId}/alarms`（QoS 1，MqttAlarmNotifier）。没有中心时消息在 broker 上自然晾着，不影响本地采集/落库/展示；**断网不丢的真正保底是本地 SQLite + forward_buffer 排队重传**（两阶段提交，失败批次重试→重试超限丢弃，broker 重连自动重订阅（2026-08-22 删死信））。配置同步走 HTTP 中心，遥测走 MQTT，两条通道互不相干。

---

## 二、启动与生命周期（App / GatewayHost）

**Q2.1 单实例**
`App.OnStartup` 创建命名 `Mutex(true, "NitroGateway.Desktop.SingleInstance", out _ownsMutex)`；拿不到所有权（`_ownsMutex == false`）说明已有实例在跑，弹提示并 `Shutdown`。理由：现场只允许一个采集进程，**防止双写同一 SQLite**（多进程同时写同一 WAL 库会损坏/锁冲突），也避免双 MQTT 发布。

**Q2.2 启动反馈窗**
`GatewayHost.Create` + `StartAsync`（迁移 + 后台服务启动）可能要数秒，直接白屏无反馈像卡死（ADR-037 S8）。所以先 `splash.Show()` 显示 `StartupWindow`，宿主就绪后再建 `MainWindow` 并 `splash.Close()`。启动失败时 `splash.ViewModel.ShowError(ex.Message)` 把错误文案置红、隐藏进度条、显示关闭按钮——用户点关闭后因 `ShutdownMode=OnLastWindowClose` 退出。

**Q2.3 全局异常兜底 + OnExit**
两条通道：`AppDomain.CurrentDomain.UnhandledException`（非 UI 线程）与 `DispatcherUnhandledException`（UI 线程）。UI 线程异常 `e.Handled = true` → 弹 `MessageBox` 提示但**不闪退**（D7 非阻塞）。`OnExit` 是兜底：若 MainWindow.Closing 没正常走完 drain（如崩溃/异常关闭），退出时补调 `_host.StopAsync()`（幂等，正常 drain 后秒回）+ `DisposeAsync()` + 释放 Mutex，避免拖脏进程与资源泄漏。

**Q2.4 StartAsync 三步**
① `MigrationRunner.Run`（FluentMigrator，复用中心同 schema 的 M001~ 迁移）——保证库结构就绪；② `IForwardMqttToggle.InitializeAsync`——把 `desktop-settings.json` 持久化的 MQTT 转发开关加载进内存（缺省/失败按启用处理，不阻断启动），让采集热路径从启动第一轮就有正确开关；③ `_host.StartAsync`——才启动全部后台服务。顺序原因：服务一启动就可能写库/发 MQTT，必须「库先就绪、开关先就位」。

**Q2.5 drain 排空**
`StopAsync` = `_host.StopAsync`（逆序）。核心是 `GatewayLifecycle` 的 drain：采集停止前最后一轮数据入 forward_buffer；然后 Forwarder 把缓冲**排空**上云（两阶段提交）；最后关 MQTT。未排完的数据留在 `forward_buffer`（SQLite），**下次启动自动续传**——这是「断网/关机不丢数据」的关键：内存态可丢，SQLite 缓冲不丢。

**Q2.6 DisposeAsync 解包**
`IHost` 只声明 `IDisposable`，实际 `Host` 实现了 `IAsyncDisposable`。直接 `_host.DisposeAsync()` 编译不过（接口上没有），所以先判 `is IAsyncDisposable` 再调用；不是则返回 `ValueTask.CompletedTask`。这是「接口只承诺最小契约、具体实现能力强则用之」的惯用写法。

---

## 三、路径与配置（DesktopPathConfig）

**Q3.1 默认目录与优先级**
缺省 `%LocalAppData%\NitroGateway`：SQLite `nitrogateway.db` + `logs/nitrogateway-desktop-.log`（日轮转保留 7 天）。优先级：**环境变量 > 设置页自定义日志目录（desktop-settings.json）> 默认**。连接串只认环境变量 `Persistence__ConnectionString`；日志路径是环境变量 `Serilog__WriteTo__N__Args__path` > 自定义目录 > 默认。设置页保存的自定义目录「重启后生效」（在 `DesktopPathConfig.Apply` 时读取并写入配置）。

**Q3.2 按 Name 定位 File sink**
`FileSinkPathKey` 遍历 `Serilog:WriteTo` 找 `Name == "File"` 的子节，用它的索引拼 `...:Args:path`；找不到回退索引 0。硬编码索引的问题：`WriteTo` 数组增删项（如加一个 Console sink）会让 File 的索引漂移，日志写到别处/读错键（ADR-027 P3-3）。`ReadLogPathEnv` 兼容索引 0 与 1：历史上移除 Console 后 File 从索引 1 变 0，为不破坏早期文档化的环境变量写法两者都认。

**Q3.3 自定义日志目录生效时机**
`SettingsViewModel.SaveLogDirectory` 校验：空值→清除恢复默认；非绝对路径→报错；`Directory.CreateDirectory` 失败→报错；通过则「加载→改 LogDirectory→保存」（合并写，保留 ForwarderMqttEnabled）。**重启后** `DesktopPathConfig.Apply` 才真正切换。损坏回退：`TryPrepareLogDirectory` 只接受绝对路径且能创建，失败回退默认目录；`DesktopSettingsStore.Load` 文件损坏 catch 后返回空设置——坏配置不阻断启动。

**Q3.4 ContentRootPath 固定 exe 目录**
WPF 应用启动目录不固定（双击 exe 时是 exe 所在目录，但从命令行/快捷方式启动时可能是别的 cwd）。固定 `AppContext.BaseDirectory` 保证 `appsettings.json` 始终从 exe 旁稳定读取，避免「换个启动方式配置就找不到/读到工作目录里的另一份」。

---

## 四、事件桥与 UI 线程模型（EventBridge / UiDispatcher）

**Q4.1 自动接入**
`DesktopServiceCollectionExtensions` 把 EventBridge 单例映射注册到三个接口；而 `IPointStoredSink` / `IDeviceHealthListener` / `IMqttStateListener` 分别是 Collection / Device / Transport 模块**既有的扩展点**（模块内遍历 DI 中所有实现者接入）。EventBridge 不关心各模块内部，只实现三个契约，靠 DI 自动接线。

**Q4.2 200ms 帧合并**
高频数据源（采集 1s、点位可能 500 个、健康/MQTT 事件）若每事件直接 `Dispatcher.BeginInvoke` 会海量抢占 UI 线程。合并成帧后：UI 只消费帧，避免每点切 Dispatcher 卡 UI（设计意图注释）。`UiFrame.IsEmpty`（Measurements/HealthChanges/MqttState/BufferBacklog 全空）时跳过发布，减少无意义调度。

**Q4.3 帧内字段语义**
`Measurements` / `HealthChanges` 是**按帧累积清空**的事件数据（来多少带多少）；`MqttState` / `BufferBacklog` 是**最近已知状态，设置后每帧携带**——消费方按文本幂等覆盖即可，不需要变更检测（ADR-027 P3-1 注释对齐实现）。BufferBacklog 只在 `_backlogDirty` 时为值、否则 null，表示「水位有变化才带」。

**Q4.4 水位轮询**
每 10 帧（200ms × 10 = 2s）`RefreshBacklogAsync` 调 `_buffer.GetCountAsync`；变化时置 `_backlogDirty = true`，下一帧携带新值，Flush 后清 dirty。只轮询不订阅：转发缓冲由 Forwarder 写，EventBridge 拿只读计数，2s 足够状态栏展示；dirty 标志避免每帧重复带相同值。

**Q4.5 帧循环自愈**
`LoopAsync` 用 `PeriodicTimer` 无限循环；catch 非取消异常后不直接退出（否则 UI 数据永久静止、无修复路径），而是记 Error 后 200ms 延迟重建 PeriodicTimer 重启循环（ADR-028 P3-1）。三层设计：重启延迟 200ms（非连续异常用户无感）、连续异常至少留日志 + 持续重试、`OperationCanceledException` 单独处理（正常释放直接 return）。

**Q4.6 UiDispatcher**
`Post`：无 WPF Application（测试）或已在 UI 线程 → 同步执行；否则 `BeginInvoke` 入队。`TryBeginInvoke` 吞异常：应用关闭中 Dispatcher 已停止受理新操作（BeginInvoke 抛异常），此时 EventBridge 帧循环仍在后台触发，不能让它崩掉——直接丢弃该次 UI 更新。动作内部异常由 Dispatcher 未处理异常通道接管。

**Q4.7 收集不加 try/catch**
`OnStoredAsync` 等只做 `lock(_gate)` + 加 List，异常面极小（契约也要求 listener 不能拖垮主路径）；加 try/catch 反而掩盖 bug。帧发布的 `FrameReady?.Invoke(frame)` 包 try/catch 记 Error：订阅方（ViewModel）异常不能影响帧循环，异常隔离在桥这一层兜底。

---

## 五、实时页性能（RealtimeViewModel）

**Q5.1 四层缓冲**
① 原始缓冲 `_rawValues`（`List<DateTimePoint>`，7200 点环形窗口，**无集合通知**——帧追加零重绘）；② 显示集合 `ChartValues`（LiveCharts 绑定，降采样 ≤1000 点）；③ 网格行 `Points`（DataGrid 绑定，表格节流批量刷）；④ `_latestByPoint` 内存最新值（`Dictionary<Guid,PointSnapshot>`，O(1) 更新、无 UI 通知，跨设备保留，切设备即时填充网格）。层层降频：原始数据不碰 UI → 显示集合 500ms 才刷 → 表格 500ms 才刷 → 内存最新值是「配置 + 最新值」即时组合。

**Q5.2 原始缓冲**
`_rawValues` 用普通 List 而非 ObservableCollection：只有选中点位的值曲线需要它，且它**不直接绑 UI**（绑的是 ChartValues），所以不需要集合通知。`MaxChartPoints=7200` = 1s 采集 × 2 小时窗口（ADR-037 S9，与预载 2h 历史对齐）。溢出 `RemoveRange(0, overflow)` 批量裁剪：一次搬移 + 单次（无通知），等价 ADR-037 S12，比逐项 RemoveAt 高效。

**Q5.3 DownsampleMinMax**
按时间把原始点均匀分到 `target/2` 个桶，每桶保留**最小值与最大值两个点**（保尖峰/谷底形状，比隔点抽稀保真），输出 ≤ `target=1000` 点（重绘成本约原 7200 的 1/7）。关键保证：首点与最新点始终在——最新点在循环末尾显式补入，所以**实时曲线右边缘 = 最新值**，不会因分桶边界把最新点丢掉。

**Q5.4 两个 500ms 节流**
图表：`OnFrame` 里 `appendChart && (now - _lastChartRefreshUtc >= ChartRefreshInterval)` 才 `RefreshChart()`（降采样 + `Replace` 单次 Reset）。表格：`now - _lastGridRefreshUtc >= GridRefreshInterval` 才 `RefreshGridFromCache()` 批量刷 DataGrid 行。`GridRefreshInterval` 是 `internal TimeSpan`（可 set）：测试可改大/改小确定性断言节流行为，生产默认 500ms。目标：把「500 点位 × 4 属性 × 5fps ≈ 1 万通知/秒」降为 ×2fps ≈ 4 千/秒，UI 线程不被刷表占满、不饿死交互（ADR-051）。

**Q5.5 IsActive 暂停**
`IsActive=false`（导航切走/窗口最小化）时 `OnFrame` 整帧丢弃（不追加、不重绘），`OnIsActiveChanged(false)` 还清空原始/显示集合并摘 `_series.Values`——LiveCharts 无数据可持有，顺带缓解切页资源不回收（LiveCharts2 #1468）。恢复可见时：重载设备下拉（ADR-048）+ 重载选中点位 2h 窗口 + `RefreshGridFromCache` 一次补齐失焦期间的表格值。由 `MainViewModel.OnSelectedNavChanged` / `SetRealtimeVisible` 置位，仅实时页在前台才激活曲线。

**Q5.6 内存最新值缓存（ADR-050）**
旧实现：切换设备触发 `QueryLatestAsync` 扫全历史 `ROW_NUMBER` 窗口（随 30 天保留量线性变慢，ADR-047 遗留项）。现在：EventBridge 每 200ms 帧携带所有已存储点位，`_latestByPoint` 以点位 Id 为键 O(1) 维护、**不随设备切换清空**；切设备时网格用「配置 + 内存最新值」即时填充。仅当某设备存在从未在帧中出现过的点位（冷启动/离线）才后台 `QueryLatestAsync` 兜底，且**只填仍缺失的点位——帧数据更新鲜，以帧为准、不覆盖**。

**Q5.7 Task.Run 移出 UI 线程（ADR-047）**
`Microsoft.Data.Sqlite` 的 async 实为「同步外包」：`QueryAsync` 在调用线程同步跑完才返回已完成 Task；连接串 `Asynchronous` 关键字已在 10.x 移除，无法从连接串侧真异步。所以在 UI 线程直接 await 会**冻结窗口**（尤其历史查询/切设备扫全表时）。`Task.Run(() => _store.QueryLatestAsync(...))` 把查询移出 UI 线程；异步期间先捕获 deviceId/pointId 到局部变量，避免 await 期间被切走读到可变属性。

**Q5.8 _loadVersion 防过期**
设备/点位切换或发起新查询时 `_loadVersion++`；异步回调 `_ui.Post` 内校验 `version != _loadVersion` 则丢弃——旧设备/旧点位的晚到结果不污染新页面（ADR-027 P1-1）。为什么不合并：新旧结果语义不同（不同设备/点位的值没有可比性），静默丢弃最安全，代价是极端情况下丢一次旧数据（可接受）。

**Q5.9 ApplyDeviceDiff 增量对账（ADR-048）**
① 倒序 `RemoveAt` 移除已不存在的设备（保留顺序）；② 新增追加末尾、重命名替换对应项（`DeviceOption` 是 record 需换新实例）；③ 选中设备仍存在则按 Id 重指向最新项（重命名后旧实例已不在集合，ComboBox 会丢显示），被删除则清空选中。**不清空重建**避免打断 ComboBox 选中；仅在实例变化时重设 `SelectedDevice`，避免无谓重载点位。

**Q5.10 RingObservableCollection**
`ObservableCollection` 没有 `RemoveRange`，逐项 `RemoveAt(0)` 会产生 N 次 `CollectionChanged` 和 O(n·k) 搬移。`TrimFront` 直接操作底层 `List`（`Items`）`RemoveRange` + 单次 `Reset` 通知；`Replace` 清空 + AddRange + 单次 Reset。LiveCharts/DataGrid 只重排一次（ADR-037 S12 / ADR-045 P2）。测试可直接断言 `CollectionChanged` 次数。

---

## 六、MVVM 与表单（DeviceEditor / PointEditor / PointBatchEditor）

**Q6.1 三路分流**
`ToDevice` 的 `Parameters` 按协议三路：Modbus 写 `UnitId/DataFormat`（+RTU 时 `Transport/BaudRate/Parity/DataBits/StopBits`）；S7 写 `Rack/Slot/CpuType/PingAddress`；OPC UA **不写任何协议参数**（对齐 web `syncParams` 三路分流，避免切协议残留的 Rack/Slot 污染连接）。传输方式：`IsDialectEditable => IsModbus`——Modbus 可切 TCP/RTU；S7 锁 TCP、OPC UA 锁 `opc.tcp`（`DialectItems` 分别为 `["TCP"]` / `["opc.tcp"]`，禁用态锁定显示）。这是对桌面旧行为的收紧（旧版 S7 也能选 RTU，改错必连不上）。

**Q6.2 切协议联动**
`OnProtocolNameChanged`：Modbus→Dialect 非 TCP/RTU 则归 TCP、端点命中其他协议默认或空则换 `127.0.0.1:502`；S7→Dialect=TCP、端点换 `127.0.0.1:102`；OPC UA→Dialect=`opc.tcp`（仅显示）、端点换 `opc.tcp://127.0.0.1:4840`。**只替换命中其他协议默认值的端点、保留用户自定义端点**：用户已填好现场真实地址时切协议不会把地址冲掉（对齐 web onProtocolChange 语义）。

**Q6.3 OPC UA dialect=null / S7 参数**
后端 `ProtocolIdentifier.OpcUa` 无方言（`dialect=null`），所以 UI 显示 `opc.tcp` 只是给用户看，`ToDevice` 时置 null 对齐 web。S7 的 `Rack/Slot/CpuType/PingAddress` 若不落库，后端只能用默认值——S7-300/400 的 Rack/Slot 不同，连不上必失败（ADR-024 P3-1，所以 S7 必须落库）。`FromDevice` 时 OPC UA dialect 显示 `opc.tcp`、Rack/Slot 天然不写回。

**Q6.4 INotifyDataErrorInfo**
`Validate()` 全量重算 `_errors` 字典，`SetError` 只在属性错误新增/移除时发 `ErrorsChanged(propertyName)`（WPF 绑定订阅，同名错误不重复发）。字段变更 `OnXxxChanged` 全部转调 `Validate()`——字段少、全量校验开销可忽略，且协议切换联动（Dialect/Endpoint）同步重算。`HasErrors`/`GetErrors` 供窗口「保存」按钮与校验提示使用。

**Q6.5 Normalize 归一化**
早期 ComboBoxItem 绑定把选中项 `ToString()` 存库，得到脏值 `"System.Windows.Controls.ComboBoxItem: Modbus"`。`FromDevice` 用 `Normalize` 剥前缀回纯值，保证旧脏数据重编辑保存后即恢复采集（ADR-036 绑定修复）。`DeviceItem.ProtocolName` 同理用 `NormalizeProtocolName`。

**Q6.6 PointEditor 地址提示与校验边界**
`AddressHint`：Modbus `如 40001`、S7 `如 DB1.DBD0`、OPC UA `如 ns=2;i=1001`（窗口 ToolTip 展示，对齐 web 占位，避免 OPC UA 用户误填 Modbus 寄存器号）。校验只做「Name/Address 非空 + ScanInterval/Deadband 非负 + 缩放系数有限」——**协议级地址格式校验不在 PointEditor**：桌面协议感知仅到「提示与文案」；协议级校验应委托 `IAddressParser`（`IPointManager.ValidateAsync` 注释），但 Device 模块为避免依赖协议实现**尚未接线**（DESIGN.md 留白）。

**Q6.7 PointBatchEditor**
`DefaultStartAddress`：Modbus `40001` / S7 `DB1.DBD0` / OPC UA `ns=2;i=1001`（对齐 web `defaultStartAddress`）。`PreviewName`：把名称模板第一个 `{###}`（按 `#` 个数零填充）替换为 `001`。`GenHint`：递增规则提示——Modbus「寄存器数」、S7「类型字节宽度（DB 区，不支持 Bool）」、OPC UA「数值标识（i=）逐点 +1，仅支持数值标识符」。OPC UA 的 `s=`/`g=` 字符串标识不能 +1，所以批量生成只对 `i=` 数值标识递增；`OnProtocolNameChanged` 只把「命中其他协议默认值」的起始地址替换为新协议默认（`IsOtherProtocolDefault`）。

**Q6.8 IUiTimer 抽象**
ViewModel 不依赖 `System.Windows.Threading`（保持「VM 与 UI 框架无关」），只依赖 `IUiTimer`（Tick/Start/Stop）；WPF 实现 `DispatcherUiTimer` 包 `DispatcherTimer`，测试注入手动触发替身确定性驱动轮询。轮询节奏（5s）是 view 关注点，抽象让 ViewModel 可测且不背 UI 线程语义。

**Q6.9 PointsViewModelFactory scope**
对话框 `DeviceDialogService` 与 `PointsViewModelFactory` 都是 Singleton，若工厂直接构造注入 `IDeviceDialogService`/`ICsvFileService` 会构造期循环依赖。用 `IServiceScopeFactory.CreateScope()` 在 scope 内解析这些服务再构造 ViewModel，`using` 即释放。注释提醒：依赖都是 Singleton，所以 scope 在 Create 返回即释放无副作用；若未来某依赖改 Scoped，必须把 scope 生命周期上提到调用方（否则被释放后对象悬空）。

---

## 七、页面 ViewModel（设备/点位/告警/历史/设置）

**Q7.1 设备页 diff 原位更新（ADR-037 S7）**
`RefreshAsync` 拿到新目录 + 健康快照后，先对 `Items` 倒序遍历：目录里消失的移除（若选中则清空选中）、仍在的按 Id `ApplySnapshot` 原位写属性；再对新增设备 Add。`DeviceItem` 是可观察对象，属性变化自动触发 UI 刷新。好处：既有行实例不变 → **选中/滚动/焦点不丢**；避免 Clear+重建的整表闪烁。统计卡（Total/Online/Offline/TotalPoints）在刷新末尾重算。

**Q7.2 刷新节奏**
两条：① `IUiTimer` 5s 周期 `RefreshAsync`（DispatcherTimer，UI 线程）；② `OnFrame` 收到 `HealthChanges` 非空即 `RefreshAsync`（健康变更帧即时刷新，不等 5s）。`DeviceCountChanged` 事件由 DevicesViewModel 在刷新后触发，MainViewModel 订阅它更新状态栏设备数——**复用同一 5s 节奏，不再重复查目录**（ADR-037 S11）。

**Q7.3 outbox 记录**
设备增/改 → `RecordDeviceAsync`，删除 → `RecordDeviceDeleteAsync`（tombstone）；点位增/改 → `RecordPointAsync`，删 → `RecordPointDeleteAsync`。`RecordOutboxAsync` 失败只 `LogDebug` **不阻断 UI**：outbox 是「待上报索引」，本地 SQLite 才是可靠存储，写 outbox 失败不影响本地操作成功，下次同步周期会重新基于本地状态补/重试。

**Q7.4 CSV 导入/批量生成 outbox 语义**
`ImportCsvAsync` / `GenerateBatchAsync` 成功后**逐条** `RecordPointAsync`——与 Add/Edit 同语义：导入的每个点位都是独立待上报变更，同步服务按设备聚合上报时取本地最新全量状态。这样「导入 500 点」与「逐点新建」在中心侧收敛为同一套合并逻辑。

**Q7.5 历史分页与日期窗口**
`PageSize=1000`（与 `QueryPagedAsync` 上限一致），翻页用 `offset = (page-1)*PageSize`。日期窗口：`FromDate 00:00`（本地）到 `ToDate 次日 00:00`（本地），转 UTC 查询——「到次日零点」保证结束日期当天全天含在内。`HasMore = count == PageSize` 驱动下一页按钮。`_loadVersion` 防查询期间切设备/点位导致过期结果污染。快捷区间 1/3/7 天（日期级窗口）。

**Q7.6 告警只读**
桌面 `AlarmsViewModel` 展示最近 24h（活跃置顶、5s 自动刷新、严重性着色），但**只读浏览无 ack**；web 有 `POST /alarms/{id}/ack`。`AlarmRulesViewModel` 规则管理全（含禁用规则列表 + 增改删，经 `IAlarmRuleRepository`，ADR-043）。差距是功能取舍：现场告警确认（ack）属运营语义，web 中心做；桌面定位「现场看板 + 规则维护」。

---

## 八、配置同步（Sync）

**Q8.1 手动导入 vs 自动同步**
「从中心导入」= **一次性、用户触发**（设置页填中心地址/Token → 拉快照 → 确认覆盖 → 以中心为准重置本地，ADR-033 阶段 2）。「配置自动同步」= **周期性、后台服务**（`SiteConfigSyncService`：拉中心快照双向合并下发 + 上报本地 outbox，阶段 3/4）。手动导入后 `ClearAllAsync` 清空 outbox（本地与中心一致，无待上报）；自动同步在未配中心地址时静默跳过（仅手动导入模式）。

**Q8.2 中心导入三步**
① `_cache.Invalidate()` 先失效目录缓存，再 `GetAllAsync` 读最新本地（避免读到导入前旧快照）；② 移除中心快照中不存在的本地设备（`UnregisterAsync` 级联删点位）；③ 按快照逐台 `RegisterAsync`（upsert）+ `ReplacePointsAsync`（删本地多余点位、批量导入快照点位）。每台设备独立 scope：Device/Point 管理服务是 Scoped（依赖 EF/Dapper 上下文），避免长生命周期上下文跟踪污染（ADR-029）。单设备失败不中断，汇总错误返回。

**Q8.3 outbox 固定主键**
`Key`：设备行 `d|{deviceId}`、点位行 `p|{deviceId}|{pointId}`，`INSERT ... ON CONFLICT(id) DO UPDATE`。效果：同一设备/点位只有一行，**「删除后又重建」原地替换行类型**——device-delete 被新 device 行覆盖（或反之），不会同时残留 upsert 与 tombstone 矛盾记录；同步上报时天然「以最新意图为准」。

**Q8.4 SyncOnceAsync 单轮 + 双向合并**
单轮：读设置（无 CenterUrl 则 return）→ `FetchSyncSnapshotAsync` 拉快照（失败跳过）→ `ApplySnapshotAsync` 合并 → `PushPendingAsync` 上报。`ApplySnapshotAsync` 四种分支（按 UpdatedAt）：
① 中心 tombstone → 本地硬删 + 清 outbox；② 中心有本地无 → 整台导入（含点位）；③ 中心较新 → 中心版本覆盖本地 + 清 outbox（本地待上报改动被裁决丢弃）；④ 本地较新 → 保留本地，outbox 待后续上报。中心快照缺失的本地设备 = 现场临时设备，保留待上报。

**Q8.5 PushPendingAsync 聚合**
按 `DeviceId` 分组聚合为每台一条 `CenterSyncChange`：任一行是 DeviceDelete → 发 tombstone（`Deleted=true, Device=null`，点位行一并被裁决）；否则 upsert 负载 = 本地设备**全量状态** + 删除点位 Id 列表。`POST /api/configsync/push` 成功后才逐台 `ClearForDeviceAsync`（accepted/skipped/rejected 均不再重试，中心裁决差异由下次下发回写本地）。失败则 outbox 保留下次重试——**最终一致**。

**Q8.6 Token 安全链路**
`CenterSyncSettings` 的 `CenterToken` 标 `[JsonIgnore]` **不参与序列化**，落盘只写 `CenterTokenEncrypted`（DPAPI `CryptProtectData` CurrentUser 作用域加密后的 Base64）。`DpapiProtector` 用 P/Invoke（不新增 NuGet 依赖），`CryptProtectUiForbidden` 禁止弹 UI。解密失败（跨用户/损坏）按空 Token 处理不阻断设置页。旧版明文：`Load` 读到无密文但有 legacy `CenterToken` 时兼容解析并**立即改写为加密形态**保存（迁移）。

**Q8.7 SiteIdProvider（ADR-036）**
解析顺序：**配置/环境变量（`Site:Id`）> site.json > 自动生成并持久化**（`site-` + 10 位加密随机，base32 去易混淆字符）。`default` 是「未初始化」哨兵（旧版 appsettings 缺省），`IsValidSiteId` 显式排除；校验规则：小写字母/数字开头，可含连字符，≤32 位，不能为 default。GatewayHost 启动时把解析结果写回配置，Forwarder/AlarmNotifier/ConfigSync/Settings 统一取用。重新生成后「重启后生效」：采集/转发运行中仍用旧 siteId（已入内存），重启才切换。

**Q8.8 CenterConfigClient 独立 HttpClient**
中心地址/Token 是**运行时用户输入**（设置页），与 Forwarder 的固定 `HttpConnectionOptions` 解耦，所以独立使用 `HttpClient` 而非 `Transport.IHttpClient`。DI 注册 `HttpClient` Singleton 统一 `Timeout = 15s`，避免每次导入新建连接资源。失败（网络不可达 / 401 / 非 2xx / 响应格式错）都收敛为 `OperationResult` 失败不抛出，`TaskCanceledException`（非取消令牌）映射为「连接中心超时」。

---

## 九、设置与安全 / MQTT 开关 / 连接测试

**Q9.1 现场可视性**
只读展示：MQTT broker/clientId/连接状态、缓冲积压、数据库路径、日志目录、采集间隔（`Collection:IntervalMs`）、转发间隔（`Forwarder:IntervalMs`）（ADR-026 D9 现场可观测）。可编辑：中心地址/Token（从中心导入）、站点标识（编辑/重新生成）、自定义日志目录、MQTT 上云转发开关。状态栏另展示设备数（MainViewModel）。

**Q9.2 DesktopForwardMqttToggle**
持久化到 `desktop-settings.json`（`ForwarderMqttEnabled`，缺省 true）；内存态 `_enabled`（int 0/1）用 `Volatile.Read/Write` 保证采集热路径跨线程同步读（避免锁开销）。`SetEnabledAsync`「加载→改字段→保存」**合并写**：只改 ForwarderMqttEnabled、保留 LogDirectory，避免与设置页「保存日志目录」互相覆盖（反之亦然）；**持久化成功后才更新内存态**，失败返回失败且内存不变。

**Q9.3 ADR-061 只在实际变化时发事件**
`changed = 旧值 != 新值` 才 `EnabledChanged?.Invoke`。不判断的话：UI 重复点同一开关（已开再点开）会再次触发事件 → MQTT 服务断开又重连（`MqttConnectionState.Disabled` 语义下不连接、不重连，直接显示已关闭）。这是与「转发总开关」配套的行为收敛：开关只响应真实状态翻转。

**Q9.4 连接测试**
构造注入 `IProtocolDriverFactory`（桌面 GatewayHost 已注册 AddNitroProtocol），与采集引擎**共用同一驱动实现**——「测试结果 = 实际采集同一条链路」。流程：`ConnectAsync` 打通链路/串口 → `PingAsync`（最小读请求）确认从站响应。连接成功只代表链路通，不代表目标从站存在（对 UnitId 校验型从站是假阳性，ADR-023）。测试**不重试**：`RetryCount/RetryIntervalMs` 置 0，避免失败重试拖长等待（对齐 Web 语义）。

---

## 十、开放题 / 与 web 差异 / 演进

**Q10.1 能力差距**
桌面缺（web 反超）：历史曲线（web ECharts step 线，桌面仅分页表格）、串口枚举/占用状态（web SystemStatus 页有，桌面 RTU 手动填 COM）、系统状态明细（熔断/转发明细，web 更全）。（注：web 死信管理页 DeadLettersView 已于 2026-08-22 随转发简化删除——重试超限即丢弃，不再有死信可管理。）桌面独有：从中心导入、站点标识 SiteId、配置自动同步（outbox）、本机会话免登录、实时曲线（LiveCharts2，web 仅数值卡片）。差距根因：形态分工——桌面=现场采集与本地运维，web=中心管理与运营。

**Q10.2 桌面补历史曲线**
可复用：RealtimeViewModel 的降采样（`DownsampleMinMax`）、环形缓冲（`RingObservableCollection`）、500ms 节流、`IsActive` 暂停机制；HistoryViewModel 已有分页查询（`QueryPagedAsync`）。缺：按历史区间加载到曲线显示集合的适配（目前 HistoryViewModel 只填表格 Rows）、图表控件在历史页的布局/坐标轴（可复用 `RealtimeChartFactory.CreateAxes`）。核心难点是「大区间降采样」——历史可能跨天，需把 `DownsampleMinMax` 用于分页结果或改成分桶聚合查询。

**Q10.3 可改进点（示例）**
① 实时页 `_latestByPoint` 无限增长：长期运行的设备点位集合固定，泄漏有限，但设备频繁重建点位的场景可加 LRU/按设备清理；② `ConfigSyncOutboxStore` 每操作独立连接：批量导入 5000 点逐条 RecordPointAsync 会开 5000 次连接，可批量 upsert；③ `CenterSyncSettingsStore` DPAPI 跨用户不可解：可提示重新输入而非静默空 Token；④ 桌面历史查询 `Task.Run` 包装的 `QueryPagedAsync` 仍是同步 IO 外包，可评估真异步驱动；⑤ EventBridge 帧与 Dispatcher 在最小化时仍每 200ms 触发（虽然 ViewModel 丢弃），可加全局暂停。

**Q10.4 手动导入清 outbox 的权衡**
手动导入 = 用户**显式确认**（对话框提示「本地现有配置将被覆盖，未上报改动无法恢复」）以中心为准重置本地，完成后 `ClearAllAsync`——语义是「本地与中心已一致，无待上报」。会不会丢未上报改动？会，但这是用户知情选择：现场改动了但没来得及同步的配置被中心版本覆盖。权衡：手动导入给「从中心推全量」的强一致路径，自动同步给「现场优先 + 最终一致」的宽松路径，二者并存覆盖不同运维场景。

**Q10.5 三条时序（口述骨架）**
① 启动：App.OnStartup（Mutex → StartupWindow）→ GatewayHost.Create（路径/Serilog/模块注册）→ StartAsync（MigrationRunner → toggle.Initialize → _host.StartAsync）→ MainWindow；后台服务（采集→存储事件→EventBridge 200ms 帧）→ UiDispatcher → 各页 ViewModel。
② 断网配置同步：设备编辑 → IDeviceManager → outbox 记一行 → SiteConfigSyncService 周期（未联网 Fetch 失败跳过，outbox 保留）→ 联网 → FetchSyncSnapshot → ApplySnapshot 双向合并 → PushPending 聚合上报 → 成功 ClearForDevice → 中心下次下发差异回写。
③ 实时帧链路：采集存 SQLite → PointStoredEvent → EventBridge 累积 → 200ms Flush 出 UiFrame → RealtimeViewModel.OnFrame（IsActive 校验 → _latestByPoint 更新 → 选中点位入 _rawValues）→ 500ms 表格节流批量刷 DataGrid + 500ms 图表节流 DownsampleMinMax → ChartValues.Replace → LiveCharts 重绘。

---

## 十一、开放题自测点（对照 questions.md「一页速记」）

能把「一页速记」里每一条展开讲出**代码位置 + 为什么**，再补上三条时序，即吃透 Desktop 模块。面试被追问「双端全栈」时，桌面侧讲这三个故事最加分：单实例 + drain 关闭（数据不丢）、实时页四层缓冲降频（UI 不卡）、outbox 双向配置同步（断网也能改配置、联网最终一致）。
