# Storage 模块「吃透」面试题

> 目的：这套题不是考背诵，而是逼你把 `src/NitroGateway.Storage`（纯接口层）和它的实现 `src/NitroGateway.Persistence`（SQLite）**每一个接口、每一条边界、每一个设计决策**读进脑子里。
> 全部答完后，你应该能不看代码讲清楚：为什么这样设计、不这样会怎样、换一种方案会出什么问题。

## 使用说明

1. 先凭记忆答题，**不要马上翻代码**；卡住的地方才看「提示」里的文件，读透后再答。
2. 每题按三层回答：**是什么 → 为什么 → 不这样会怎样**。
3. 答完再对文末「参考要点」，对不上的回到代码里追根。
4. 通关标准：
   - 能默写 `IDeviceRepository`、`IPointRepository`、`IMeasurementStore`、`IForwardBuffer` 全部方法签名与语义；
   - 能画出 Buffer 状态机并讲出每个状态转换的 SQL；
   - 能解释每个「为什么」（EF vs 裸 SQL、WAL、两阶段提交、OperationResult、O 格式时间戳……）；
   - F 组动手实验全部完成。

## 覆盖范围（读题前先通读）

- 接口层：`src/NitroGateway.Storage/`（`Configuration/`、`TimeSeries/`、`Buffer/`，共 3 个接口 + 1 个死信 DTO【停用保留】+ README/DESIGN.md）
- 实现层：`src/NitroGateway.Persistence/Sqlite/`（`SqliteDeviceRepository`、`SqlitePointRepository`、`SqliteMeasurementStore`、`SqliteForwardOutbox`、`SqlitePragmas`、`SqliteErrorClassifier`、`MeasurementRetentionService`）
- 领域模型：`src/NitroGateway.Domain/`（`Device`、`DevicePoint`、`PointSnapshot`、`BatchMeasurements`、`MeasurementRecord`、`QualityCode`）
- 迁移：`src/NitroGateway.Persistence/Migrations/M001~M004`
- 测试：`tests/NitroGateway.UnitTests/Sqlite*Tests.cs`、`MeasurementRetentionServiceTests.cs`

---

## A. 模块边界与分层

**A1.** Storage 模块为什么是「纯接口」？实现放在哪个项目？接口与实现之间的依赖方向是什么？（提示：`src/NitroGateway.Storage/DESIGN.md` 原则、`AGENTS.md` 雷区）

**A2.** Storage 下为什么拆成 `Buffer/`、`Configuration/`、`TimeSeries/` 三个子项目（三个独立 csproj）而不是一个？各自的核心消费者是谁？（提示：`Buffer/README.md`、`TimeSeries/README.md`、`Configuration/README.md`；`Forwarder`、`Collection`、`Device`、`Webapi` 谁用谁）

**A3.** 接口方法返回值大量使用 `OperationResult` / `OperationResult<T>` 而不是抛异常。为什么？哪些错误是「业务流程的一部分」？（提示：`src/NitroGateway.Shared` 的 `OperationResult`；`SqliteForwardOutbox` 每个方法都 try/catch 归约；`DataDispatcher` 失败降级分支）

**A4.** 「接口只增不删」是哪里的纪律？破坏它会有什么后果？（提示：`AGENTS.md` 雷区第 5 条；`IMeasurementStore.QueryLatestAsync` 就是「只增」的实例，见 ADR-002 P2-4）

**A5.** Storage 接口直接引用 Domain 类型（`Device`、`DevicePoint`、`PointSnapshot`、`BatchMeasurements`），这违反「Domain 不引用基础设施」吗？反过来：Domain 引用 Storage 了吗？（提示：看两个 csproj 的 ProjectReference 方向）

**A6.** 几乎所有方法都有 `CancellationToken ct = default` 参数，为什么在这个网关项目里尤其重要？（提示：采集 1s 周期、优雅关闭 drain；`NitroGateway.Host` 生命周期）

## B. Configuration（设备/点位配置存储）

**B1.** 默写 `IDeviceRepository` 的全部方法。`SaveAsync` 的 upsert 语义是什么——Id 已存在时覆盖哪些字段？（提示：`src/NitroGateway.Storage/Configuration/IDeviceRepository.cs`；实现 `src/NitroGateway.Persistence/Sqlite/SqliteDeviceRepository.cs`）

**B2.** `GetByIdAsync` 设备不存在时返回什么？接口注释和实现是怎么对齐的？（提示：`IDeviceRepository.cs` 注释；`SqliteDeviceRepository.GetByIdAsync`；ADR-005 P3-1）

**B3.** `DeleteAsync` 删除不存在的设备返回成功（幂等删除）。为什么？删除设备时它的点位怎么处理？（提示：`SqliteDeviceRepository.DeleteAsync`；`NitroGatewayDbContext` 的 `DeleteBehavior.Cascade`）

**B4.** `IPointRepository.SaveAsync(Guid deviceId, DevicePoint point, ...)` 为什么要显式传 `deviceId`？点位表靠什么字段归属设备？（提示：`IPointRepository.cs`；`PointEntity.DeviceId` 外键）

**B5.** `SaveBatchAsync` 为什么存在？它解决了什么性能问题？「单事务」靠什么保证？批次里出现重复 Id 会怎样？（提示：`IPointRepository.cs` 注释（CSV 导入/批量生成）；`SqlitePointRepository.SaveBatchAsync`；EF Core `SaveChanges` 默认单事务；ADR-005 P2-1）

**B6.** `DeleteAsync(deviceId, pointId)` 为什么是双条件？不加 `deviceId` 条件会有什么 bug？（提示：`SqlitePointRepository.DeleteAsync` 的 SQL 条件）

**B7.** 为什么 Configuration 用 EF Core，而 TimeSeries / Buffer 用裸 SQL（Dapper）？这两种存储的访问模式有什么本质区别？（提示：`src/NitroGateway.Storage/DESIGN.md` 约束 1；`SqliteMeasurementStore` 类注释；`SqliteForwardOutbox` 类注释）

## C. TimeSeries（时序数据存储）

**C1.** 默写 `IMeasurementStore` 的全部方法，并分类：哪些是写、哪些是查、哪些是管理？（提示：`src/NitroGateway.Storage/TimeSeries/IMeasurementStore.cs`）

**C2.** `WriteAsync` 为什么设计成「批量写」？实现里如何批量？（提示：采集 1s 周期每轮产生大量快照；`SqliteMeasurementStore.WriteAsync`：单事务 + Dapper 单条多值 INSERT；空列表直接成功）

**C3.** `timestamp` 用什么格式存储？为什么这样存？这对查询排序和范围比较意味着什么？（提示：`SqliteMeasurementStore.WriteAsync` 里 `ToString("O")` + UTC；M001 迁移注释「字典序即时间序」；读回时 `DateTimeStyles.RoundtripKind`）

**C4.** `value` 和 `raw_value` 分别存什么？为什么 `value` 统一转 `double`，转不了存什么？`raw_value` 为什么用 JSON 存？（提示：`PointSnapshot.RawValue/Value` 注释（寄存器数组 vs 工程值）；`SqliteMeasurementStore.Serialize/Deserialize`）

**C5.** `QueryLatestAsync` 两个分支（`pointId` 非空 / null）的 SQL 分别是什么？为什么不用「拉最近 1 小时全量再内存过滤」？（提示：`SqliteMeasurementStore.QueryLatestAsync`；`GROUP BY point_id` + `MAX(timestamp)` join；ADR-002 P2-4）

**C6.** `QueryPagedAsync` 的 `limit` 为什么要夹紧 `[1, 1000]`？`offset` 呢？上层调用（`MeasurementsController.History`）默认值是多少？（提示：`SqliteMeasurementStore.QueryPagedAsync` 的 `Math.Clamp`；ADR-005 P2-2）

**C7.** `PurgeAsync` 干什么？谁在什么时机调用它？（提示：`SqliteMeasurementStore.PurgeAsync`；`src/NitroGateway.Persistence/Sqlite/MeasurementRetentionService.cs` 及其测试）

**C8.** 每个操作都新建 `SqliteConnection` 而不是共享一个长连接，为什么？（提示：`SqliteMeasurementStore` 类注释；ADR-001 P1-4：Collection/Forwarder/Alarm 跨线程并发）

**C9.** `idx_measurements_query (device_id, point_id, timestamp)` 这个复合索引分别支撑了哪些查询？`QueryByDeviceAsync` 只用 `device_id` 时能走索引吗？（提示：M001 迁移；复合索引最左前缀原则）

## D. Buffer（转发缓冲/重试超限丢弃）

**D1.** `IForwardBuffer` 解决的核心问题是什么？「断电不丢」靠什么保证？（提示：`Buffer/README.md`；`IForwardBuffer` 类注释；WAL 模式）

**D2.** 画出 Buffer 的状态机（含全部状态与转换），并说出每个转换对应的 SQL 操作。（提示：`SqliteForwardOutbox` 类注释：Pending → InFlight → 删除 / 失败回 Pending / 超限直接 DELETE 丢弃；M002、M004 迁移）

**D3.** `EnqueueAsync` 入队时存了什么？payload 是什么格式？（提示：`SqliteForwardOutbox.EnqueueAsync`：CamelCase JSON 的 `BatchMeasurements`，初始 `retry_count=0`）

**D4.** 解释「两阶段提交」：`DequeueAsync` 为什么「查询但不删除」？`CommitAsync` 在什么时候被谁调用？如果不先标 `InFlight` 会怎样？（提示：`SqliteForwardOutbox.DequeueAsync`：SELECT Pending + 同事务 UPDATE InFlight；`CommitAsync`：DELETE；Forwarder 成功转发后调用；DESIGN.md 约束 5）

**D5.** `InFlight` 状态存在的意义是什么？去掉它会引入什么问题？（提示：并发/多实例重复消费、崩溃后重复转发；对比「已取出未确认」窗口）

**D6.** 进程崩溃后遗留的 `InFlight` 批次会怎样？在哪个时机恢复？恢复失败会阻断启动吗？（提示：`SqliteForwardOutbox` 构造函数启动恢复：UPDATE InFlight → Pending；try/catch 仅警告）

**D7.** `MarkFailedAsync` 的 `retry_count` 语义：一次失败如何计数？超过 `maxRetries`（默认几？）后怎么处置（2026-08-22 起直接丢弃）？`last_error` 什么时候写/清？（提示：`SqliteForwardOutbox.MarkFailedAsync` 先 DELETE 再 UPDATE 回 Pending）

**D8.** 出队时发现 payload 反序列化失败（损坏行）会怎么处理？会不会把整批都卡死？（提示：`SqliteForwardOutbox.DequeueAsync` 里的 `RecoverCorruptRowAsync` → 复用 `MarkFailedAsync`；继续处理其余行）

**D9.** `DeadLetterEntry` 现在是什么状态？为什么接口里还留着？（提示：`DeadLetterEntry.cs`【停用】注释；`IForwardBuffer` 接口只增不删；原设计只带最小字段不含设备名）

**D10.** `Count` 和 `GetCountAsync` 有什么区别？为什么后来新增了 `GetCountAsync`？（提示：`SqliteForwardOutbox.Count` 是同步查 DB；ADR-001 P3-13：async 路径避免同步阻塞）

**D11.** `idx_forward_buffer_status (status, enqueued_at)` 支撑了什么？出队 SQL 的 `ORDER BY enqueued_at ASC` 为什么能保证 FIFO？（提示：M002 迁移；出队 SQL）

## E. 设计决策深挖

**E1.** `SqlitePragmas.Apply` 设置了哪三个 PRAGMA？各自解决什么问题？为什么必须在事务外调用？（提示：`src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs`；WAL 读写并行、synchronous=NORMAL 兼顾持久性与性能、busy_timeout 防锁冲突报错）

**E2.** `SqliteErrorClassifier` 把哪些 SQLite 错误码映射成什么？为什么 `SQLITE_FULL(13)` 才表示磁盘满，而 `IOERR(10)`/`CORRUPT(11)` 归为通用 Storage 错误？（提示：`SqliteErrorClassifier.cs`；ADR-002 P3-4；`SqliteErrorClassifierTests.cs`）

**E3.** 为什么实现层每个方法都 try/catch 归约为 `OperationResult` 而不是向上抛？对比：`SqliteDeviceRepository`（EF）为什么反而**不**捕获异常？（提示：`SqliteForwardOutbox` 各方法 vs `SqliteDeviceRepository.SaveAsync` 注释「由上层统一处理」——两种策略的适用场景）

**E4.** 如果要把 SQLite 换成 PostgreSQL / InfluxDB / TimescaleDB：哪些文件必须改？哪些文件一行都不用动？接口为什么能保证这一点？（提示：DESIGN.md 原则；NuGet 包各实现自持；`NitroGateway.Storage` 无实现依赖）

**E5.** `WriteAsync` 里的 `Activity` 追踪是干什么的？打了哪些标签？（提示：`SqliteMeasurementStore.WriteAsync`；`NitroGateway.Telemetry.Tracing.GatewayActivities.SqliteWrite`；Prometheus + 追踪的关系）

**E6.** 采集 1s 写、前端查询、Alarm 评估同时访问同一个 SQLite 文件，为什么不会互相卡死或报「database is locked」？（提示：WAL + busy_timeout + 每操作独立连接 + 短事务的组合拳）

**E7.** Buffer 的 payload 为什么整批存 JSON，而不是把每条测量拆成行存？拆行存储会破坏什么？（提示：批量整体入队/出队/提交的原子性；重试/丢弃只需整批摘要；`DeadLetterEntry` 从 payload 反序列化取 DeviceId/RecordCount）

**E8.** DESIGN.md 约束 4 说「单批不超过 1000 条以避免锁表」——代码里由谁保证这个约束？去 `MeasurementWriteHost` / 采集分发链路查一查：批量来自有界 Channel（容量 1000 批），写入前有没有分块？如果一批超过 1000 条会发生什么？（提示：`src/NitroGateway.Collection/Dispatcher/MeasurementWriteHost.cs`；`SqliteMeasurementStore.WriteAsync` 本身不分块——这是个值得记录的隐患还是已由上游保证？）

## F. 动手验证

**F1.** 跑通与 Storage 相关的全部测试，并说出每个测试文件覆盖了什么：
`dotnet test tests/NitroGateway.UnitTests --filter "FullyQualifiedName~Sqlite"`（含 `SqliteForwardOutboxTests`、`SqliteMeasurementStoreTests`、`SqliteErrorClassifierTests`、`SqliteAlarmRepositoryTests`）

**F2.** 用 `SqliteForwardOutboxTests` 的方式写一个临时测试：入队 3 批 → 出队 2 批 → 模拟进程崩溃（直接 new 一个新 buffer 实例）→ 验证遗留 InFlight 被重置为 Pending 并仍可出队。

**F3.** 写一个测试走完「重试超限丢弃」全流程：入队 → 连续 `MarkFailedAsync` 超过 `maxRetries` → 断言行被物理删除（RowExists=false）且 `forward_total{status="dropped"}` 上报；期间用 `last_error` 断言失败原因被记录（参考 `SqliteForwardOutboxTests.MarkFailed_OverMaxRetries_Drops` / `_ReportsDroppedMetric`）。

**F4.** 给 `DequeueAsync` 加断点单步走一遍：确认 SELECT 和 UPDATE 在**同一个事务**里提交，然后对比「先 SELECT 后单独 UPDATE（无事务）」会发生什么竞态。

**F5.** 关闭所有代码窗口，默写四件事（写下来，和代码逐字对）：
1. 三个接口的全部方法签名；
2. Buffer 状态机全图 + 每个转换的 SQL；
3. `measurements` 表与 `forward_buffer` 表的所有列（含迁移加的列）；
4. `SqlitePragmas` 的三个 PRAGMA 与 `SqliteErrorClassifier` 的错误码映射。

---

## 参考要点（答完再对）

### A 组
- Storage 纯接口、零依赖（只引用 Domain/Shared）；实现统一在 `NitroGateway.Persistence`，各自 NuGet 自持，互不污染（DESIGN.md）。
- 三子项目 = 三类存储职责：配置（低频 CRUD）、时序（海量追加）、缓冲（FIFO 持久队列）；消费者分别是 Device 管理、Collection 写/Webapi 查、Collection 写/Forwarder 取。
- `OperationResult` 让「失败」成为可返回的业务结果，热路径可降级；存储实现把所有 SQLite 异常归类为 `OperationalError` 不抛出。
- 「接口只增不删」保证所有消费者与实现不受破坏；`QueryLatestAsync`、`SaveBatchAsync`、`GetCountAsync` 都是新增式演进的实例。
- 不违反：依赖方向是 Storage → Domain，Domain 不引用任何基础设施；Storage 接口里出现 Domain 类型正是接口层存在的意义。
- `CancellationToken` 支撑 1s 采集周期内的取消与 Host 优雅关闭 drain。

### B 组
- `IDeviceRepository`：Save/Delete/GetById/GetAll/GetByStatus 五个方法；Save 是 upsert（Find 存在则 SetValues 覆盖，否则 Add）。
- 不存在返回 `Failure(General)`，接口注释与实现一致（ADR-005 P3-1 对齐）。
- Delete 幂等成功；设备删除级联删点位（`DeleteBehavior.Cascade`）。
- 点位强制归属设备（外键 DeviceId）；Delete 双条件防跨设备误删。
- `SaveBatchAsync` 单事务批量 upsert（EF SaveChanges 默认事务），替代逐条往返；批次内重复 Id 以最后一条为准，重复的新 Id 会主键冲突失败。
- EF 适合低频、关系复杂的配置 CRUD；时序/缓冲是高频追加 + 极简 SQL，EF 跟踪开销不划算，走 Dapper 裸 SQL。

### C 组
- `IMeasurementStore`：Write（写）、Query/QueryByDevice/QueryPaged/QueryLatest（查）、Purge（管理）。
- 批量写 = 单事务 + Dapper 多值参数化 INSERT；空列表直接成功；单批 ≤1000 是设计约束（见 E8）。
- 时间戳用 UTC 的 `O` 格式字符串：字典序 = 时间序，字符串比较即时间范围比较；读回 RoundtripKind 再 ToUniversalTime。
- `value` 统一 double（工程值，不可转换存 NULL）；`raw_value` JSON 存原始值（含寄存器数组 ushort[]），读回优先按数组解析、失败原样返回字符串。
- Latest：pointId 非空走 `ORDER BY timestamp DESC LIMIT 1`；null 走 `GROUP BY point_id` + `MAX(timestamp)` join，每个点位一条——替代「拉 1 小时全量内存过滤」（ADR-002 P2-4）。
- Paged：limit 夹紧 `[1,1000]`、offset ≥0；History 接口默认 limit=1000（ADR-005 P2-2）。
- Purge 删除 `timestamp < before` 的数据，由 `MeasurementRetentionService` 定期调用做空间管理。
- 每操作独立短连接（+PRAGMA）避免跨线程共享连接；单例注入的是实现，不是连接。
- 复合索引 (device_id, point_id, timestamp) 支撑设备+点位+时间范围查询；最左前缀也覆盖仅 device_id 的查询。

### D 组
- Buffer 解决断网/重启不丢待转发数据；WAL + 事务保证写入即持久化。
- 状态机：`Pending → InFlight → 删除`；失败 `InFlight → Pending`（retry+1）；`retry_count ≥ maxRetries(默认5) → 直接 DELETE 丢弃`（2026-08-22 简化，原为 DeadLetter）；启动时遗留 InFlight 全部重置 Pending。
- 入队存 CamelCase JSON 的 `BatchMeasurements`，初始 Pending、retry_count=0。
- 两阶段提交：Dequeue 在**同一事务**内 SELECT Pending + UPDATE InFlight；Forwarder 成功后才 CommitAsync DELETE；未确认不删（DESIGN.md 约束 5）。
- InFlight 防止并发/重复消费同一批；崩溃后由构造函数启动恢复兜底，恢复失败仅警告不阻断启动。
- MarkFailed 先 DELETE（retry_count+1>=max，命中即丢弃）再 UPDATE（未命中回 Pending，retry_count+1、last_error）；UPDATE 必须带 `SET status='Pending'`，否则批次卡 InFlight。
- 损坏 payload：Dequeue 中反序列化失败的行走 `RecoverCorruptRowAsync` → 复用 MarkFailed 逻辑（重试/丢弃），其余行正常出队，不整批卡死。
- DeadLetterEntry 与死信方法【停用】保留（接口只增不删）；原最小字段设计（无设备名）供未来复用。
- `Count` 是同步查 DB 的兼容属性；async 路径用 `GetCountAsync` 避免阻塞（ADR-001 P3-13）。
- (status, enqueued_at) 索引支撑「按状态 + FIFO 序」出队；enqueued_at 为 O 格式字符串，升序即入队序。

### E 组
- PRAGMA：`journal_mode=WAL`（读写并行，库级持久）、`synchronous=NORMAL`（兼顾持久与写性能，连接级）、`busy_timeout=5000`（并发写锁等待 5s 而非立即报错）；WAL 切换必须在事务外。
- 错误码：13→StorageFull（真磁盘满）；10/11→Storage 通用（I/O/损坏，消息注明）；5→DatabaseLocked；其余/非 SqliteException→Storage。
- 高频热路径（时序、缓冲）把异常归约为 OperationResult 供上层降级；低频配置仓储（EF）不吞异常，交给上层统一处理——两种策略按调用场景取舍。
- 换库只改 `Persistence` 实现（新 NuGet 包），Storage 接口、Domain、所有消费者零改动；这就是纯接口层的价值。
- Activity 追踪让每次 SQLite 写入出现在 Prometheus/追踪里（表名、快照数、错误标签），方便定位采集写库瓶颈。
- 并发安全 = WAL（读写并行）+ busy_timeout（写锁等待）+ 独立短连接 + 短事务，四者缺一不可。
- payload 整批 JSON 保证批量入队/出队/提交原子且转发语义完整；拆行会引入「半批」一致性问题，摘要/丢弃语义也失去意义。
- E8 结论：`SqliteMeasurementStore.WriteAsync` 本身不分块；批量来自 `MeasurementWriteHost` 的有界 Channel（容量 1000 批）。若上游某批快照数量可能超 1000 条，需要确认分发链路是否分块，否则大事务会持锁更久——这正是「设计约束靠调用方保证」的典型例子，值得去 Collection 模块核实。

### F 组
- F1 的过滤命令能跑通且全部绿，是吃透的前提；每个测试文件都对应本套题的若干条边界。
- F2~F4 验证的是 D2/D4/D6/D7/D8 的答案，做不出来说明还没真懂状态机与事务边界（F3 已随死信删除改为「丢弃」流程）。
- F5 是最终验收：默写对不上，就把 B/C/D 组的题重做一遍。
