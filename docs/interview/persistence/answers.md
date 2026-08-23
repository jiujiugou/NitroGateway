# Persistence 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：Storage 的 README / DESIGN.md 部分描述已过时（如仓储生命周期、`AddNitroSqlite` 签名），以代码 + XML 注释为准。

---

## 一、架构与分层

**Q1.1 Storage 与 Persistence 分工**
`Storage` 只有接口（Configuration：设备/点位；TimeSeries：时序；Buffer：缓冲），零实现、零重依赖；`Persistence` 是 SQLite 实现（EF Core + Dapper + FluentMigrator）。换库时接口层不动：新增一个实现项目实现同一组接口，DI 换注册即可；所有调用方（Collection / Forwarder / Webapi）只依赖接口。这正是 AGENTS.md「Storage 是纯接口，接口只增不删」的价值——云端可换 PostgreSQL / InfluxDB / TimescaleDB（`Storage/DESIGN.md` 原则）。

**Q1.2 EF Core vs Dapper 的边界**
- EF Core 用于配置类表（devices/points/alarms/alarm_rules）：低频 CRUD、需要导航属性（设备→点位）、级联删除、请求内事务/跟踪。
- Dapper + 手写 SQL 用于 measurements 与 forward_buffer：1s 采集热路径，需要精确控制批量 INSERT、LIMIT 出队、UPDATE 状态机；EF 的映射/跟踪开销与行为控制在这里是负担。
- 边界在 `NitroGatewayDbContext` 类注释明确写出：时序与缓冲**不在 DbContext 内**，走 Dapper 独立连接。

**Q1.3 DI 生命周期**
- EF 仓储（Device/Point/Alarm）Scoped：与 DbContext 同生命周期，天然适配 Web 请求与 Alarm 事件 scope。
- Dapper 存储（MeasurementStore/ForwardBuffer）Singleton：本身无状态（只持有连接串 + 日志），可共享；**每个操作内部新建独立连接**（ADR-001 P1-4），绝不共享 Singleton 连接——避免 Collection/Forwarder/Alarm 跨线程并发使用同一连接导致 `database is locked` 或数据错乱。代价是每操作一次连接打开/PRAGMA，本地文件 SQLite 可接受。

**Q1.4 接口只增不删**
`IForwardBuffer` 早期有同步 `Count` 属性（每次查库 `ExecuteScalar`，async 路径会阻塞线程）；演进时不能删（破坏所有实现与调用方），改为**新增** `GetCountAsync` 并保留 `Count`，注释引导 async 路径走新方法（ADR-001 P3-13）。收益：接口稳定、实现可平滑演进；代价：接口里残留一个「不该用」的成员，靠注释与 Code Review 约束。这就是「只增不删」的典型成本。

**Q1.5 DomainMapper**
双向映射 Domain ↔ EF 实体：枚举（DeviceStatus / DataType / PointAccess / AlarmSeverity）与 Guid 全部转字符串存储；`ConnectionParams`（Dictionary）序列化为 CamelCase JSON。空参数序列化为 `"{}"`（保持列非空语义），反序列化空串返回空字典（调用方免判空）。注意 `ToDomain` 用 `Enum.Parse`，库中存了非法枚举字符串会抛——属于实现边界。

---

## 二、SQLite 并发与 PRAGMA

**Q2.1 三条 PRAGMA**
- `journal_mode=WAL`：读写并行（读不阻塞写、写不阻塞读），匹配「1s 采集写 + 前端查询 + Alarm 并发」；WAL 是**库级**持久设置（写入库头，一次设置长期生效）。
- `synchronous=NORMAL`：WAL 下比 FULL 快，断电最多丢最近已提交数据但库不会损坏（WAL 原子性保证）。
- `busy_timeout=5000`：SQLite 单写者，写写互斥；等待 5s 而不是立刻报 `database is locked`。synchronous / busy_timeout 是**连接级**，每次开连接都要 Apply。

**Q2.2 逐条执行**
`Microsoft.Data.Sqlite` 对多语句批处理的支持不可靠（官方建议逐条），拼成一条 `"PRAGMA ...; PRAGMA ...; PRAGMA ..."` 可能只执行第一条或行为未定义。逐条执行保证三条都生效。

**Q2.3 O 格式时间字符串**
- 全部存 UTC 的 `O` 格式（`DateTime.ToString("O")`，含 7 位小数与偏移），长度固定、同一 UTC 时刻的字符串表示一致 → **字典序即时间序**。
- 收益：范围查询直接字符串 `BETWEEN`，无需函数转换；`ORDER BY timestamp` / `MAX(timestamp)`（QueryLatestAsync 的每点最新）直接利用字典序；跨数据库（PG/Influx）可移植。
- 前提：写入与查询两侧都先 `ToUniversalTime()` 再 `ToString("O")`，读取侧 `DateTimeStyles.RoundtripKind` 解析后再 `ToUniversalTime()`。

**Q2.4 锁模型与锁冲突**
WAL 下：读读/读写并行，**写写互斥**（单写者）。并发写来源：采集批量写 measurements + Forwarder 出队/提交/标记失败 + Alarm 写告警。busy_timeout 让后到的写者排队最多 5s；超时 → SQLITE_BUSY(5) → `SqliteErrorClassifier` → `DatabaseLocked` → OperationResult 失败 → 调用方走降级。发现手段：日志出现「(数据库锁定)」上下文 + 对应 Activity Error 标签；持续出现说明写竞争严重，需调批量窗口/错峰。

---

## 三、时序数据 measurements

**Q3.1 表结构**
`id`（GUID 字符串 PK）、`device_id` / `point_id`（字符串）、`point_name`（冗余点位名）、`raw_value`（JSON，存寄存器数组等复合原始值）、`value`（double 工程值，可空）、`data_type`（字符串）、`timestamp`（O 格式 UTC 字符串）、`quality`（质量码字符串）、`error_msg`（可空）。
冗余 point_name / data_type：快照**自描述**，查询无需 join devices/points 就能展示与过滤；ADR-002 P1-3 修复过「写入空串导致列存在但数据丢失」的问题。

**Q3.2 WriteAsync 细节**
1. 空列表直接成功（不建连接）。
2. 单事务：`BeginTransactionAsync` → 一次 Dapper `ExecuteAsync` 批量 INSERT（多行参数）→ `CommitAsync`。
3. `raw_value` JSON 序列化（CamelCase）；`value` 经 `IConvertible` 转 double，失败存 `DBNull`。
4. `timestamp` 统一 UTC O 格式；`quality` / `data_type` 字符串化。
5. Activity：`GatewayActivities.SqliteWrite` + tags（TableName、SnapshotCount）；失败 `SetStatus(Error)` + ErrorMessage tag。
6. catch → `RollbackAsync` → `SqliteErrorClassifier.Classify` 返回 Failure，**不抛出**。

**Q3.3 复合索引**
`idx_measurements_query (device_id, point_id, timestamp)`：支撑 `WHERE device_id=? AND point_id=? AND timestamp BETWEEN ...`（最左前缀：等值条件后做范围扫描），也支撑 `ORDER BY timestamp`。timestamp 放最后：只有范围列放末尾，索引才能先按等值列收敛再扫范围。同思路：`forward_buffer (status, enqueued_at)` 支撑「按状态 + FIFO 出队」。

**Q3.4 QueryLatestAsync**
- pointId 非空：`ORDER BY timestamp DESC LIMIT 1`（该点位最新一条）。
- pointId 为 null：子查询 `GROUP BY point_id` 取 `MAX(timestamp)`，再自连接取整行——每点一条最新。
替代旧做法：控制器「拉 1 小时全量到内存再过滤」，大结果集浪费 IO/内存（ADR-002 P2-4）。`MAX(timestamp)` 字符串比较即最大时间（依赖 Q2.3 的字典序）。

**Q3.5 分页夹紧**
`Math.Clamp(limit, 1, 1000)`、`Math.Max(0, offset)`。防客户端传超大 limit 一次拉全表（内存/网络压力），防负 offset 引发 SQL 错误；接口文档明确要求实现侧夹紧（ADR-005 P2-2），默认 1000 与旧全量行为接近。

**Q3.6 反序列化与失败契约**
- 时间：`DateTime.Parse(..., RoundtripKind).ToUniversalTime()`（O 格式往返保真）；`value` / `error_msg` 判 `DBNull` → null；`raw_value` JSON 反序列化；`quality` `Enum.Parse`。
- 写/查失败统一 `Classify` 返回 OperationResult：保证调用方（控制器、测量写入宿主）的降级分支可达，而不是异常击穿（ADR-002 P1-1）。

---

## 四、转发缓冲 forward_buffer

**Q4.1 表结构与状态机**
M002 建表：`id`（批次 GUID 字符串 PK）、`payload`（BatchMeasurements CamelCase JSON）、`status`（默认 'Pending'）、`enqueued_at`（O 格式 UTC）；M004 追加 `retry_count`（默认 0）、`last_error`（可空）。索引 `(status, enqueued_at)` 支撑 FIFO 出队。
状态机：`Pending`（待转发）→ Dequeue → `InFlight`（转发中）→ Commit 删除 / MarkFailed 回 `Pending`（重试+1）/ 超过 `maxRetries`（默认 5）直接 DELETE 丢弃（2026-08-22 简化，原为进 DeadLetter）；启动时遗留 InFlight 重置为 Pending。

**Q4.2 两阶段提交**
DequeueAsync 在同一事务内：SELECT Pending 批次（LIMIT）→ UPDATE 标记 InFlight → 提交后才把数据交给 Forwarder。目的：
- 出队即占用（InFlight），多消费者/崩溃后重启不会重复取同一批；
- 数据只有确认成功（Commit）才删除——出队时删掉，转发失败就是**丢数**；
- 语义是 at-least-once：崩溃后 InFlight 恢复为 Pending 重新发送，最多重复不丢失。

**Q4.3 启动恢复**
构造函数执行 `UPDATE forward_buffer SET status='Pending' WHERE status='InFlight'`：上次进程异常退出遗留的批次若留在 InFlight，不计入 Count、不再出队 = **静默丢数**，所以必须恢复。
恢复失败只 `LogWarning` 不阻断启动：数据库异常时网关仍能启动（下次启动继续尝试恢复）；若构造函数抛异常会导致整个 DI 失败、服务起不来，代价更大。
实现细节（ADR-018 P3-5）：构造函数不立刻执行恢复，改为首次被使用时经 `EnsureRecoveredAsync` 异步完成，恢复完成前其余操作等待同一闸门（避免构造函数里做 IO + 启动竞态）。

**Q4.4 损坏行恢复**
Dequeue 提交事务后逐行反序列化；损坏行（JsonException / null）调用 `RecoverCorruptRowAsync`：复用 `MarkFailedAsync`（重试+1、记 last_error、超限直接丢弃），让坏行进入正常失败路径而不是卡死 InFlight；恢复自身失败只记日志，**不影响其余行出队**（P0-1②）。

**Q4.5 MarkFailedAsync 合并往返**
先 `DELETE WHERE id=@id AND retry_count+1 >= maxRetries`（命中即丢弃，2026-08-22 简化，替代原进 DeadLetter）；未命中再 `UPDATE SET status='Pending', retry_count+1, last_error=@reason`——丢弃判定与状态迁移分开，但「计数 + 状态迁移」仍在同一条 UPDATE，无并发窗口。
注意：UPDATE 必须带 `SET status='Pending'`（曾经丢失该字段导致批次卡 InFlight 不再重试，已修复）。
原实现 3 次往返（查/改/判）→ 2 次（ADR-001 P2-11）。

**Q4.6 死信三操作**
- 现状（2026-08-22 起）：死信三操作【停用】——转发重试超限改为直接丢弃，不再产生 DeadLetter 状态，运行期不调用；保留实现仅因 `IForwardBuffer` 接口只增不删。
- 原语义（供理解）：`GetDeadLettersAsync` 按 `status='DeadLetter' ORDER BY enqueued_at LIMIT` 查询，payload 反序列化失败按空批次兜底；`RetryDeadLetterAsync` 仅当 `status='DeadLetter'` 时重置为 Pending（retry_count=0、last_error=NULL），影响 0 行 → NotFound；`DiscardDeadLetterAsync` 仅 DeadLetter 才物理删除，0 行 → NotFound。
- 带状态条件设计：只能操作死信，防止误操作正常批次（如重试一个还在转发的批次）。

**Q4.7 Count / GetCountAsync / BufferRow**
`Count` 是接口历史遗留的同步属性（每次开连接 `ExecuteScalar`），保留兼容，注释明确「async 路径请用 GetCountAsync」；`GetCountAsync` 用 `ExecuteScalarAsync` + `CommandDefinition(ct)`，避免同步阻塞线程（ADR-001 P3-13）。`BufferRow` 只投影 id + payload：避免把整行（status/retry_count/last_error）反序列化进内存，payload 出队后按需解析。

---

## 五、迁移与运维

**Q5.1 为什么 FluentMigrator**
版本化迁移表（VersionInfo）+ 幂等（已执行自动跳过）+ Up/Down 可回滚；`EnsureCreated()` 无版本管理，只适合空库一次性建表，无法演进已有库。执行时机：应用启动一次（`DatabaseInitializationExtensions.InitializeDatabase`），`MigrationRunner.Run` 传入连接串与从 DI 取的 logger（ADR-002 P3-2，迁移/备份日志不再丢弃）。

**Q5.2 Run 完整步骤**
1. 开临时连接 + `SqlitePragmas.Apply`（WAL 库级设置就位）。
2. 库文件已存在 → `BackupDatabase`：`wal_checkpoint(TRUNCATE)` → 复制到 `backups/`（时间戳命名）→ 清理只留 5 份；备份失败抛异常 → **启动失败**。
3. `AddFluentMigratorCore` + ScanIn 程序集 → `MigrateUp()`（幂等）。
4. `RecordVersion`：UPSERT `app_meta` 写入入口程序集版本（x.y.z）；M006 表不存在（旧库）时捕获只记 Debug，不阻断启动。

**Q5.3 备份一致性**
WAL 下已提交数据可能仍在 `-wal` 文件里，直接 `File.Copy` 主库文件会缺最近提交数据或拿到不一致快照；先 `wal_checkpoint(TRUNCATE)` 把 WAL 合并回主库再复制。备份失败让启动失败：迁移是结构变更，出错可能半迁移，**没有备份就没有回退现场**，宁可失败（ADR-002 P3-3）。只留 5 份：备份是整库复制，长期运行磁盘会膨胀，按时间戳命名便于人工恢复最近现场。

**Q5.4 M003 历史遗留**
M003 的 PascalCase 列名与后续 snake_case 不一致，但它是**已执行迁移**：FluentMigrator 版本表已记录，改了也不会重跑，且改列名会破坏既有库。纪律：已发布的迁移永远只追加不修改；Down 只在未发布/开发期使用；新表统一 snake_case（ADR-002 P3-1）。类注释保留了该决策说明，防御性 `Schema.Exists` 也一并保留。

**Q5.5 app_meta**
key-value 元数据表（key PK、value、updated_at），当前只存 `app_version` 供运维/诊断。写入用 UPSERT：`INSERT ... ON CONFLICT(key) DO UPDATE SET value=..., updated_at=...`（SQLite 3.24+）。M006 未执行（旧版本库）时表不存在 → 捕获异常只记 Debug 跳过，保证向后兼容不阻断启动。

**Q5.6 M007 迁移要点**
- 新文件 `Migrations/M007_Xxx.cs`，`[Migration(7)]`，版本号单调递增，不修改 M001~M006。
- Up：`Alter.Table("forward_buffer").AddColumn("expire_at").AsString().Nullable()` + `Create.Index(...)`；SQLite ALTER TABLE 只能加列，注意 **NOT NULL 列必须带默认值**，无法在一条语句里改列类型/删列。
- Down：`Delete.Column("expire_at").FromTable(...)` + 删索引。
- 需要回填数据时：先加列再 `UPDATE` 全表回填（幂等可重入）。
- 迁移后记得在测试/开发库验证，靠真实迁移跑一遍而不是只编译。

---

## 六、错误处理与 OperationResult

**Q6.1 错误码映射**
- 13 SQLITE_FULL → `StorageFull`（磁盘满）
- 10 SQLITE_IOERR → `Storage`（I/O 错误）
- 11 SQLITE_CORRUPT → `Storage`（数据库损坏，消息注明）
- 5 SQLITE_BUSY → `DatabaseLocked`
- 其他 → `Storage`
IOERR/CORRUPT 不是 StorageFull：只有 13 才表示磁盘满，误标会导致告警与处理动作错误（扩容 vs 检修磁盘/恢复备份）；损坏在消息中注明便于区分（ADR-002 P3-4）。

**Q6.2 三种失败策略并存**
- Dapper 存储（MeasurementStore/ForwardBuffer）：catch → Rollback → Classify → 返回 Failure。理由：热路径/后台路径必须自包含降级，保证 DataDispatcher 等调用方的失败分支真正可达（P0-2）。
- 告警仓储（EF）：捕获后用 `DbUpdateException.InnerException` 解包出真正的 SqliteException 再 Classify——EF 会把 SQLite 异常包一层，不解包就归类成 General。
- 设备/点位仓储：SaveAsync 明确**不捕获**，EF 异常抛给上层统一处理（Web 请求路径有中间件）。三种策略的分界：后台/热路径自包含，请求路径交给框架层。

**Q6.3 磁盘满链路**
采集/转发写入 → SQLITE_FULL(13) → `StorageFull` → OperationResult Failure → 调用方降级（缓冲入队失败记录、转发失败进重试/丢弃、告警跳过），同时 Activity Error + 日志「(磁盘满)」。区分价值：StorageFull 提示扩容/清理（备份、保留策略、VACUUM），Storage 提示检修磁盘或恢复备份——告警与处置动作不同。测试：`SqliteErrorClassifierTests` 覆盖各错误码映射。

**Q6.4 失败处理三层次**
- **阻断启动**（迁移备份失败）：不可回退、影响数据一致性 → 宁可失败。
- **仅告警**（InFlight 恢复失败、保留清理失败）：不影响核心功能，服务先起来，下次周期自动重试。
- **返回 Failure**（业务读写失败）：调用方有降级路径，错误语义随 OperationResult 传递。
判断标准：失败后果是否可自动恢复、是否影响启动与核心链路。

---

## 七、EF 仓储与领域映射

**Q7.1 EF 映射**
devices/points：M003 建表，**PascalCase 列名历史遗留**；devices（Id PK、Name 200、ProtocolName 100、Endpoint 500、超时/重试默认值、Status 50、ConnectionParams JSON），points（Id PK、DeviceId FK→devices.Id、Name/Address 200、DataType/Access 50、Enabled、ScanIntervalMs、Deadband、ScaleFactor/Offset），`HasIndex(DeviceId)`；`OnDelete(DeleteBehavior.Cascade)`（删设备级联删点位）。alarms/alarm_rules：M005 建表，**snake_case**，DbContext 里逐列 `HasColumnName`；索引 `(device_id, point_id)`、`(device_id, state)`、`occurred_at DESC`。

**Q7.2 SaveAsync upsert**
`FindAsync(主键)` 查重：不存在 → `Add(ToEntity(...))`；存在 → `CurrentValues.SetValues(ToEntity(...))` 覆盖标量属性 → `SaveChanges`。限制：`SetValues` 只更新标量，**不处理导航属性**——设备仓储不保存点位（点位归 IPointRepository），点位归属由 SaveAsync 的 deviceId 参数强制。

**Q7.3 SaveBatchAsync 单事务**
一次查询 existing（`ids.Contains`）→ 内存分派 Add/SetValues → 一次 `SaveChanges`（EF 默认单事务），把 CSV 导入从 N 次往返降为 1 次（ADR-005 P2-1）。重复 Id 两种情形：批次内已存在的 Id 多次出现 → 最后一次覆盖（SetValues 重复执行）；批次内**不存在**的重复 Id → `SaveChanges` 时插入两条相同 PK → 主键冲突失败（注释明示）。

**Q7.4 删除语义**
- 设备删除：`FindAsync` 不存在 → Success（幂等删除）；存在 → Remove + SaveChanges，级联删点位。
- 点位删除：`FirstOrDefault(Id && DeviceId)` 双条件——防误删**其他设备下同名 Id** 的点位；不存在 → Success。
区别：设备删除靠级联，点位删除靠双条件定位。

**Q7.5 GetAllAsync 与缓存**
`GetAllAsync` 每次全量加载全部设备 + Include 点位：设备多时内存/IO 放大，且被 1s 采集轮询频繁调用（热路径）。ADR-002 P2-2 引入 `DeviceSnapshotCache`（Singleton，TTL 10s 兜底）：DeviceManager.GetAllAsync 走缓存，注册/注销/状态变更/点位增删改（含批量部分成功）均 Invalidate；运行状态改读 `IDeviceHealthMonitor` 实时快照（状态与配置解耦），无快照的历史设备回退配置 Status。

---

## 八、后台任务与数据生命周期

**Q8.1 RetentionService**
参数：`retentionDays` 默认 30（最小 1）、`interval` 默认 24h（最小 1s，测试可注入小间隔）；从配置 `Persistence:MeasurementRetentionDays / MeasurementRetentionInterval` 注入。流程：循环 `PurgeOnceAsync` → `Task.Delay(interval, stoppingToken)`，OCE break（正常停机）；PurgeOnce：`UtcNow.AddDays(-retentionDays)` → `PurgeAsync(before)` → 成功 Info / 失败 Error 日志。

**Q8.2 可降级 vs 不可降级**
保留清理是**维护操作**：失败只是磁盘多占一点，服务照常，下周期自动重试 → 只记日志。迁移备份是**一致性操作**：结构变更失败可能半迁移，无备份无法回退 → 阻断启动。判断标准：失败是否影响数据一致性与启动安全，是否可自动恢复。

**Q8.3 大 DELETE 风险**
单条 `DELETE FROM measurements WHERE timestamp < @before` 会：长时间持有写锁（阻塞其他写者）、大事务、WAL 膨胀、可能一次删百万行。改进方向：分批删除（`LIMIT` 循环小事务）、按时间段分表（如 measurements_YYYYMMDD + 视图，SQLite 无原生分区）、低峰执行、`PRAGMA optimize` 整理。现状 v1 单条实现可接受，能指出风险与方向即为深水分。

---

## 九、可观测性与测试

**Q9.1 Activity 追踪**
写入 span：`GatewayActivities.SqliteWrite`，tags：`TableName=measurements`、`SnapshotCount`；成功 `SetStatus(Ok)`，失败 `SetStatus(Error)` + `ErrorMessage` tag。查询/清理路径**没有独立 span**——可观察性缺口：读慢/清理慢无法从链路追踪定位，可提改进（为 Query/Purge 加 span）。

**Q9.2 测试覆盖**
- `SqliteMeasurementStoreTests`：写入/查询/分页夹紧/最新值/清理。
- `SqliteForwardOutboxTests`：入队/出队/提交/失败重试/超限丢弃/启动恢复/GetCountAsync。
- `SqliteErrorClassifierTests`：错误码 → OperationalError 映射。
- `SqliteAlarmRepositoryTests`：告警/规则 CRUD + EF 异常解包分类。
- `MeasurementRetentionServiceTests`：周期执行/取消/失败不中断。
- `MeasurementWriteHostTests`（跨模块）：Channel 批量写落库。

**Q9.3 真库 vs mock**
- 行为类（启动恢复、超限丢弃、分页夹紧、FIFO 顺序）：用**真 SQLite**（临时文件/内存库）——事务、索引、约束行为真实，才能验证状态机与 SQL。
- 上层（ForwarderEngine/控制器）：mock `IForwardBuffer` / `IMeasurementStore` 接口，测编排不测存储。
- 红绿对照：先写断言期望行为的测试（红）→ 实现/修复 → 绿。
- 并发类行为：不靠真实并发压测（不稳定），而是**确定性构造状态**（如直接插入 InFlight 行再触发恢复），断言结果。

---

## 十、扩展与设计权衡

**Q10.1 换 PostgreSQL 的改动面**
接口层不动（这正是 Storage 纯接口的意义）。实现层需重写：
- Dapper SQL 审计：参数语法基本一致，但时间列建议用 `timestamptz`（O 字符串在 PG 中可读性/性能差）；
- UPSERT 语法：SQLite `ON CONFLICT` → PG `ON CONFLICT ... DO UPDATE`（语义相似但细节不同）；
- `wal_checkpoint` / `PRAGMA`：SQLite 专属，备份逻辑移到实现侧（PG 用 pg_dump/物理备份）；
- EF 部分：换 `Npgsql.EntityFrameworkCore.PostgreSQL` Provider，映射基本复用。
结论：接口 + 测试是资产的保护伞，SQL 细节是迁移成本主体。

**Q10.2 断网 24h 链路**
采集 → `EnqueueAsync` 落盘（SQLite 文件，断电不丢）→ Forwarder 每 5s `Dequeue`（Pending→InFlight）→ MQTT 失败 `MarkFailed`（重试+1 回 Pending）→ 超 5 次直接丢弃并上报 dropped（2026-08-22 简化，不阻塞新数据）→ 恢复后 FIFO 继续发；本地 measurements 有保留策略控磁盘。主要风险是 **buffer 表磁盘增长**：批次量 = 采集频率 × 点数 × 断网时长，需评估 vs 磁盘容量。数据不丢靠 buffer（可靠），历史可视化靠 measurements（尽力而为）——两级语义不同。

**Q10.3 大表优化方向**
① 保留策略调优（窗口与间隔）；② 按时间分表（measurements_YYYYMMDD + 视图/路由，SQLite 无原生分区）；③ 降采样（原始 1s 短窗保留，聚合 1min/1h 长窗）；④ 索引按实际查询瘦身；⑤ 低峰 `PRAGMA optimize` / VACUUM。代价：分表与降采样增加查询路由与写入复杂度，需权衡「查询兼容性 vs 存储成本」。

**Q10.4 写放大与批量窗口**
写量评估：1s 采集 × N 点 = N 行/s 写入（单写者 SQLite）。Collection 的 `MeasurementWriteHost`（有界 Channel 1000，DropOldest）异步聚合成批 → `SqliteMeasurementStore.WriteAsync` 单事务批量 INSERT：窗口越大单批吞吐越高，但内存缓冲与延迟越大；DropOldest 是「本地历史尽力而为」的显式取舍（云转发走 Buffer 是可靠链路，两套语义对照 Q10.2）。面试亮点：能讲清「批量窗口是吞吐与延迟/丢数窗口的旋钮」。

**Q10.5 接口只增不删是绝对的吗**
不是「永远不能删」，而是**删的代价极高**（所有实现、测试、调用方一次性迁移，且无版本化机制兜底）。安全演进路径：标记 `[Obsolete]` + XML 注释说明 → 内部调用点全部迁到新 API → 大版本/明确决策时删除并同步更新所有实现与测试。本项目 `Count` 的处理（保留 + 新增 GetCountAsync + 注释引导）就是「只增不删 + 文档引导」的实例；能说出这条路径，说明理解破坏性变更的管理。
