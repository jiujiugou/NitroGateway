# Device 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 10 组 45 题；参考答案见 `answers.md`。

---

## 一、架构与职责边界

**Q1.1 ★** Device 模块的定位是什么？哪些职责明确**不在**本模块（说出至少 5 个）？
代码定位：`DESIGN.md` 定位段 + 「不负责」表。

**Q1.2 ★** `IAddressParser` 和 `PointAddress` 为什么定义在 Protocol 层而不是 Device/Domain？有哪些消费方？
代码定位：`DESIGN.md` 地址解析段；`IPointManager.ValidateAsync` 注释。

**Q1.3 ★** `DevicePoint` 与 `PointSnapshot` 为什么拆成两个对象？各自承担什么？
代码定位：`src/NitroGateway.Domain/Devices/DevicePoint.cs`、`PointSnapshot.cs` 类注释。

**Q1.4 ★★** `AddNitroDevice` 中哪些服务注册为 Singleton、哪些为 Scoped？各自的理由是什么？
代码定位：`DeviceServiceCollectionExtensions.cs`。

---

## 二、设备生命周期（DeviceManager）

**Q2.1 ★** `RegisterAsync` 成功后的副作用链：Save → Evict → UpdateStatus → Invalidate，每一步解决什么问题？
代码定位：`DeviceManager.cs:32` 起（42/44/46 行）。

**Q2.2 ★★** 「Status 唯一入口」约束：代码里到底有哪些路径会修改 `Device.Status`？HealthMonitor 为什么不直接写库？
代码定位：`DeviceManager.cs:74` 起；`IDeviceHealthMonitor.cs` 类注释（SST）。

**Q2.3 ★★** `UpdateStatusAsync` 的幂等短路（状态相同直接返回）除了省一次写库，还避免了什么副作用？
代码定位：`DeviceManager.cs:83`。

**Q2.4 ★★** `SetMaintenanceAsync(deviceId, false)` 为什么恢复为 `Unknown` 而不是 `Online`？
代码定位：`DeviceManager.cs:95` 起。

**Q2.5 ★★** `UnregisterAsync` 的清理三连（Evict / Remove / Invalidate）分别防什么？漏掉任何一个会出什么问题？
代码定位：`DeviceManager.cs:50` 起（56/57/59 行）。

**Q2.6 ★★★** `RegisterAsync` 内部实际是 `SaveAsync`（upsert）——「注册」与「更新配置」的边界在哪？这带来什么隐患？
代码定位：`DeviceManager.cs:32`、`RegisterAsync` 内注释「设备新建或更新」。

---

## 三、健康判定（DeviceHealthMonitor）

**Q3.1 ★** 状态机：什么条件迁移到 Offline / Online？为什么需要成功、失败两套计数？防抖（de-bounce）语义是什么？
代码定位：`DeviceHealthMonitor.cs:44` / `:68`；测试 `DeviceHealthMonitorTests`。

**Q3.2 ★★** 触发阈值后计数器为什么立即重置？已处于目标状态时为什么不会重复通知？
代码定位：`DeviceHealthMonitor.cs:57` / `:81`（TryRemove）、`:59` / `:85`（状态守卫）。

**Q3.3 ★★** `ReportSuccess` 开头为什么 `_failures.TryRemove`？`ReportFailure` 开头为什么 `_successes.TryRemove`？
代码定位：`DeviceHealthMonitor.cs:46` / `:70`。

**Q3.4 ★★** 并发上报下 `ConcurrentDictionary.AddOrUpdate` 的计数精确吗？会不会出现「两个线程同时看到 count == 阈值」？
代码定位：`DeviceHealthMonitor.cs:47` / `:71`。

**Q3.5 ★★** `UpdateStatus`（同步快照）与 `Report*` 的配合：`RegisterAsync` 里先调 `_healthMonitor.UpdateStatus(...)` 的意义是什么？不调会怎样？
代码定位：`DeviceManager.cs:44`；`DeviceHealthMonitor.cs:103`。

**Q3.6 ★★** `Remove` 清理了什么？不清理的后果？（内存/幽灵设备）
代码定位：`DeviceHealthMonitor.cs:117` 起；测试 `Remove_ClearsCountersAndSnapshot`。

**Q3.7 ★★★** `NotifyListeners` 为什么 fire-and-forget？异常隔离靠什么保证？这里面藏着什么隐患？
代码定位：`DeviceHealthMonitor.cs:132` 起；`IDeviceHealthListener.cs` 注释。

**Q3.8 ★★★** `DeviceStatus` 枚举有 `Error`，但 HealthMonitor 只迁移 Online/Offline——这是设计还是缺口？`LastError` 会落库吗？
代码定位：`DeviceStatus.cs`；`DeviceHealthSnapshot.LastError`；`DeviceHealthMonitor` 全文件。

---

## 四、快照缓存（DeviceSnapshotCache）

**Q4.1 ★** 这个缓存解决什么问题？为什么说 TTL（10s）只是兜底而不是主机制？
代码定位：`DeviceSnapshotCache.cs:27` / `:66`；`IDeviceSnapshotCache.cs` 类注释（ADR-002 P2-2）。

**Q4.2 ★★** 它是 Singleton，为什么注入 `IServiceScopeFactory` 而不是 `IDeviceRepository`？（captive dependency）
代码定位：`DeviceSnapshotCache.cs:15` / `:45`。

**Q4.3 ★★** `SemaphoreSlim` + 双检（double-check）解决什么问题？为什么不能用 `lock`？
代码定位：`DeviceSnapshotCache.cs:18` / `:35` / `:38` / `:42`。

**Q4.4 ★★** `Invalidate` 的调用点有多少处？逐条列出。漏掉一处会怎样？「事件驱动失效 + TTL 兜底」这个双层设计好在哪？
代码定位：`DeviceManager.cs:46/59/90`；`PointManager.cs:36/44/53/75/88`。

**Q4.5 ★★★** 缓存返回的是可变 `Device`（`Status` 有 setter），接口注释说「不得修改」——这是不是隐患？怎么加固？
代码定位：`IDeviceSnapshotCache.cs` 注释；`Device.Status` setter。

---

## 五、点位管理（PointManager）

**Q5.1 ★** 点位增/删/改成功后除了写库还做了什么？为什么？
代码定位：`PointManager.cs:25` / `:40` / `:48`。

**Q5.2 ★★** `ImportAsync` 为什么先 `SaveBatchAsync`，失败再逐条保存？部分成功时为什么也要 `Invalidate`？
代码定位：`PointManager.cs:57` 起（65/75/88 行）；ADR-005 P2-1 注释。

**Q5.3 ★★** `ValidateAsync` 现在校验了哪些字段？协议级地址校验为什么没做？
代码定位：`PointManager.cs:106` 起；`IPointManager.cs` 注释。

**Q5.4 ★★★** `ImportAsync` 回退路径的语义问题：批量失败后逐条保存，可能出现「部分成功但整体返回失败」——调用方如何感知哪些成功？这是缺陷还是合理设计？
代码定位：`PointManager.cs:79` 起。

---

## 六、批量服务（PointBatchService）

**Q6.1 ★** `ParseCsv` 的必填列与容错行为：坏行、DataType 解析失败分别怎么处理？
代码定位：`PointBatchService.cs:31` 起。

**Q6.2 ★★** `ParseCsvRows` 的引号状态机支持什么？字段内逗号/换行/双引号（`""`）分别怎么处理？CRLF 呢？
代码定位：`PointBatchService.cs:95` 起。

**Q6.3 ★★** `ExportCsv` 的 `EscapeCsv` 与导入侧 `ParseCsvRows` 是否对称？round-trip 能保证吗？
代码定位：`PointBatchService.cs:175` / `:260`。

**Q6.4 ★★** `Generate` 的地址步进为什么用 `DataType.RegisterCount()`？5000 上限的意义？名称模板 `{###}` 的语义？
代码定位：`PointBatchService.cs:214` 起；`DataTypeExtensions.cs`。

**Q6.5 ★★★** `Generate` 把地址序列化为纯数字字符串——对 Modbus 没问题，对 S7 / OPC UA 呢？这是设计边界还是缺陷？
代码定位：`PointBatchService.cs:237`；`PointAddress` 各协议解析器。

---

## 七、事件与监听（Events / Listeners）

**Q7.1 ★** 完整事件回路：从 Collection 上报健康信号到状态落库，经过哪些组件？
代码定位：`DeviceHealthMonitor.cs:132`；`PersistenceListener.cs`；`HealthListenerRegistrar.cs`。

**Q7.2 ★★** `PersistenceListener` 为什么用 `IServiceScopeFactory` 而不是直接注入 `IDeviceManager`？
代码定位：`PersistenceListener.cs:15` 起。

**Q7.3 ★★** `HealthListenerRegistrar` 为什么用 IHostedService 在启动时注册，而不是在 DI 注册方法里直接 AddListener？
代码定位：`HealthListenerRegistrar.cs`；`DeviceServiceCollectionExtensions.cs`。

**Q7.4 ★★** 单个 Listener 抛异常的影响面？谁负责兜底？
代码定位：`IDeviceHealthListener.cs` 注释；`PersistenceListener.cs:36` 起。

---

## 八、跨模块联动

**Q8.1 ★★** HealthReporter（Collection）与 HealthMonitor（Device）的职责边界？「读成功但部分点位质量差」时，熔断器与健康上报分别怎么决策？
代码定位：`DeviceCollector` 步骤 4/5；`DeviceHealthMonitor` 只收 Report 信号。

**Q8.2 ★★** 维护模式过滤为什么以 HealthMonitor 实时快照为准，而不是设备配置里的 Status？
代码定位：`IDeviceSnapshotCache.cs` 注释（采集器维护过滤不读缓存 Status）；测试 `DeviceCollectorMaintenanceTests`。

**Q8.3 ★★★** `DevicePoint.Deadband` 注释「值变化小于死区不触发上报」与 `PointValuePipeline` 实际行为矛盾——实际是什么？哪边是准？（ADR-008 P1-1 漂移题）
代码定位：`DevicePoint.cs` Deadband 注释；`PointValuePipeline.ConvertSingle` 死区分支。

**Q8.4 ★★** `GetAllAsync` 走内存缓存、`GetByStatusAsync` 走仓储——两个入口读到的不一致会有什么影响？「配置读取」与「运行状态读取」应该各走哪条路？
代码定位：`DeviceManager.cs:63` / `:70`；`IDeviceSnapshotCache.cs` 注释。

---

## 九、开放题（找问题 / 演进）

**Q9.1 ★★★** 不看答案，列出 Device 模块至少 3 个可改进点，并说明理由（可从并发、异常、语义、持久化角度想）。
代码定位：全模块。

**Q9.2 ★★★** `UpdateStatusAsync` 的 read-modify-write 是原子的吗？两个并发调用（如维护接口 + 健康事件）会怎样？怎么修复？
代码定位：`DeviceManager.cs:74` 起。

**Q9.3 ★★★** 设备抖动时健康事件可能高频触发，`PersistenceListener` 写库会成为瓶颈吗？现有防护有几层？还能怎么优化？
代码定位：`DeviceHealthMonitor.cs` 阈值/重置/守卫；`DeviceManager.cs:83` 幂等短路。

**Q9.4 ★★★** 如果要支持「设备分组采集」或「多租户」，Device 模块哪些接口/模型需要先变？
代码定位：`IDeviceManager` / `IDeviceSnapshotCache` / `DeviceHealthSnapshot`。

**Q9.5 ★★★** 不看代码，画出「设备上线 → 连续 3 次失败 → 离线 → 自愈 → 恢复上线」的完整时序，标注每个参与方（Collection/HealthMonitor/PersistenceListener/DriverPool/缓存）。
代码定位：全模块 + Collection 相关测试。

---

## 十、一页速记（答完自检）

- 状态只能通过 `UpdateStatusAsync` 改；HealthMonitor 只计数、只发事件
- 防抖：任何一次成功重置失败计数、失败重置成功计数；阈值 3/3 触发，触发后计数清零
- 已处于目标状态不重复通知（状态守卫）
- 配置写入（注册/注销/点位增删改/状态变更）→ `Evict` 驱动 + `Invalidate` 缓存，联动不拆
- 缓存：Singleton + scope factory 取仓储；SemaphoreSlim 双检；TTL 10s 只兜底
- 维护结束恢复 `Unknown`，由采集轮重新探测
- 批量导入：先事务批量，失败逐条回退，部分成功也失效缓存
- Listener：fire-and-forget + 各自 try/catch，异常互不影响
