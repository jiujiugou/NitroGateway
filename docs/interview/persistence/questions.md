# Persistence 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 10 组 48 题；参考答案见 `answers.md`。

---

## 一、架构与分层（Storage 接口 / Persistence 实现 / DI）

**Q1.1 ★** Storage 与 Persistence 的分工是什么？如果要把 SQLite 换成 PostgreSQL 或 InfluxDB，改动面在哪里？
代码定位：`src/NitroGateway.Storage/DESIGN.md`；`src/NitroGateway.Storage/Buffer/IForwardBuffer.cs:11`、`src/NitroGateway.Storage/TimeSeries/IMeasurementStore.cs:12`。

**Q1.2 ★** 同一个数据库，为什么配置数据（devices/points/alarms）走 EF Core，而 measurements 和 forward_buffer 走 Dapper 手写 SQL？两套技术栈的边界在哪里？
代码定位：`src/NitroGateway.Persistence/Sqlite/NitroGatewayDbContext.cs` 类注释；`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs`、`SqliteForwardOutbox.cs`。

**Q1.3 ★★** DI 注册：为什么 EF 仓储是 Scoped、Dapper 存储是 Singleton？Singleton 下跨线程安全如何保证？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteServiceCollectionExtensions.cs:25`。

**Q1.4 ★★** 接口纪律「只增不删」在缓冲接口上的体现？保留 `Count` 同步属性、新增 `GetCountAsync` 的代价与收益？
代码定位：`src/NitroGateway.Storage/Buffer/IForwardBuffer.cs:11`；`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs` 的 `Count` / `GetCountAsync`。

**Q1.5 ★★** DomainMapper 的职责？枚举、Guid、ConnectionParams 各以什么形式存储？为什么空参数序列化为 `"{}"`？
代码定位：`src/NitroGateway.Persistence/DomainMapper.cs:11`。

---

## 二、SQLite 并发与 PRAGMA

**Q2.1 ★★** SqlitePragmas.Apply 的三条 PRAGMA 各解决什么问题？为什么说 WAL 是库级设置、synchronous/busy_timeout 是连接级设置？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs:18`。

**Q2.2 ★★** 为什么三条 PRAGMA 要逐条执行，而不能拼成一条多语句命令？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs` 循环注释。

**Q2.3 ★★★** 时间戳为什么存 `O` 格式的 UTC 字符串而不是 INTEGER/DATETIME？「字典序即时间序」成立的前提是什么？BETWEEN 范围查询如何利用这一点？
代码定位：`src/NitroGateway.Persistence/Migrations/M001_CreateMeasurementsTable.cs` 类注释；`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:84`（QueryAsync）、`:218`（QueryLatestAsync）。

**Q2.4 ★★★** 1s 采集写 + 前端查询 + Alarm 并发场景下，SQLite 的锁模型是什么？busy_timeout=5000 超时后会发生什么？如何从日志/指标发现「锁冲突」？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs` 类注释；`src/NitroGateway.Persistence/Sqlite/SqliteErrorClassifier.cs:13`（SQLITE_BUSY=5）。

---

## 三、时序数据 measurements

**Q3.1 ★** measurements 表每列的含义？为什么冗余存储 point_name / data_type 而不 join 点位表？
代码定位：`src/NitroGateway.Persistence/Migrations/M001_CreateMeasurementsTable.cs`；`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:33`（INSERT 列）。

**Q3.2 ★★** WriteAsync 批量写入的完整细节：事务、批量方式、raw_value/value 双列、空列表、失败路径。
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:33`。

**Q3.3 ★★** 复合索引 `idx_measurements_query (device_id, point_id, timestamp)` 支撑哪些查询？timestamp 为什么放最后？
代码定位：`src/NitroGateway.Persistence/Migrations/M001_CreateMeasurementsTable.cs`。

**Q3.4 ★★** QueryLatestAsync 的两条 SQL 分别怎么写？它替代了原来的什么做法（ADR-002 P2-4）？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:218`。

**Q3.5 ★★** QueryPagedAsync 的 limit/offset 夹紧规则是什么？防止什么问题？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:164`；`src/NitroGateway.Storage/TimeSeries/IMeasurementStore.cs:12` 接口注释。

**Q3.6 ★★** 查询结果反序列化的细节（RoundtripKind、DBNull、Enum.Parse）？写/查失败为什么统一返回 OperationResult 而不是抛异常？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:84`、`:33`。

---

## 四、转发缓冲 forward_buffer

**Q4.1 ★** forward_buffer 表结构（M002 + M004 追加列）与批次状态机？（超限处置现在是丢弃不是死信，2026-08-22 简化）
代码定位：`src/NitroGateway.Persistence/Migrations/M002_CreateForwardBufferTable.cs`、`M004_AddDeadLetterSupport.cs`；`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs` 类注释。

**Q4.2 ★★** 两阶段提交：DequeueAsync 为什么在事务内「SELECT + UPDATE 标记 InFlight」？为什么出队时不删除？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs:117`。

**Q4.3 ★★★** 启动恢复：构造函数把遗留 InFlight 重置为 Pending 的目的？恢复失败为什么只告警不阻断启动？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs:57`。

**Q4.4 ★★★** 出队后反序列化损坏的行怎么处理？为什么不能让它留在 InFlight？恢复失败会怎样？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs:117`（步骤②）、`:195`（RecoverCorruptRowAsync）。

**Q4.5 ★★★** MarkFailedAsync 如何做到「丢弃判定 + 一次 UPDATE 完成重试计数与状态迁移」？（2026-08-22 起先 DELETE 丢弃、未命中再 UPDATE 回 Pending）原来几次往返（ADR-001 P2-11）？UPDATE 忘了 SET status='Pending' 会怎样？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs:399`。

**Q4.6 ★★** 死信三操作（GetDeadLetters / RetryDeadLetter / DiscardDeadLetter）现在是什么状态？为什么接口里还保留？原语义为什么都带 `status = 'DeadLetter'` 条件？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs:450`（【停用】起）`、:499`、`:532`。

**Q4.7 ★★** `Count` 与 `GetCountAsync` 的区别？BufferRow 为什么只投影 id + payload？
代码定位：`src/NitroGateway.Storage/Buffer/IForwardBuffer.cs:11`；`src/NitroGateway.Persistence/Sqlite/BufferRow.cs`。

---

## 五、迁移与运维

**Q5.1 ★** 为什么用 FluentMigrator 而不是 `EnsureCreated()`？迁移在什么时机执行？
代码定位：`src/NitroGateway.Persistence/MigrationRunner.cs:25`；`src/NitroGateway.Persistence/DatabaseInitializationExtensions.cs`。

**Q5.2 ★★** MigrationRunner.Run 的完整步骤？每一步失败分别怎么处理？
代码定位：`src/NitroGateway.Persistence/MigrationRunner.cs:25`、`:63`（BackupDatabase）。

**Q5.3 ★★★** WAL 模式下备份为什么必须先 `wal_checkpoint(TRUNCATE)`？备份失败为什么让启动失败？为什么只保留 5 份？
代码定位：`src/NitroGateway.Persistence/MigrationRunner.cs:63`。

**Q5.4 ★★★** M003 列名是 PascalCase，后续迁移都是 snake_case——为什么不把 M003 改掉？「已执行迁移不可变」的纪律是什么？
代码定位：`src/NitroGateway.Persistence/Migrations/M003_CreateDeviceTables.cs:13`。

**Q5.5 ★★** app_meta 表存什么？版本写入用的什么 SQL 语义？M006 未执行时如何处理？
代码定位：`src/NitroGateway.Persistence/Migrations/M006_AddAppMetaTable.cs`；`MigrationRunner` 的 `RecordVersion`。

**Q5.6 ★★** 写一个 M007 迁移（给 forward_buffer 加 expire_at 列 + 索引，含回滚）的要点？哪些坑要注意（SQLite ALTER TABLE 限制）？

---

## 六、错误处理与 OperationResult

**Q6.1 ★★** SqliteErrorClassifier 的错误码映射表？为什么 IOERR/CORRUPT 不映射成 StorageFull？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteErrorClassifier.cs:13`。

**Q6.2 ★★★** 模块内存在几种「失败处理策略」？Dapper 存储、告警仓储、设备/点位仓储为什么策略不同？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:33`；`SqliteAlarmRepository.cs:26`（Classify 解包 DbUpdateException）；`SqliteDeviceRepository.cs:24` 注释。

**Q6.3 ★★★** 磁盘满（SQLITE_FULL=13）时从写入到告警的完整链路？「StorageFull vs Storage」的区分价值？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteErrorClassifier.cs:13`；`tests/NitroGateway.UnitTests/SqliteErrorClassifierTests.cs`。

**Q6.4 ★★** 失败处理的三层次：阻断启动（备份失败）、仅告警（InFlight 恢复/保留清理）、返回 Failure（业务操作）——各自的判断标准？
代码定位：`src/NitroGateway.Persistence/MigrationRunner.cs:63`；`SqliteForwardOutbox.cs:57`；`MeasurementRetentionService.cs:34`。

---

## 七、EF 仓储与领域映射

**Q7.1 ★** devices/points 的 EF 映射：表名、列名风格、外键、级联、索引？与 alarms/alarm_rules 的映射差异？
代码定位：`src/NitroGateway.Persistence/Sqlite/NitroGatewayDbContext.cs`；`Migrations/M003_CreateDeviceTables.cs`；`Migrations/M005_AddAlarmTables.cs`。

**Q7.2 ★★** 设备 SaveAsync 的 upsert 语义怎么实现？CurrentValues.SetValues 有什么限制（导航属性）？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteDeviceRepository.cs:24`；`DomainMapper.cs:42`。

**Q7.3 ★★** SaveBatchAsync 为什么用单事务批量？批次内重复 Id 的两种情形分别怎样？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqlitePointRepository.cs:44`。

**Q7.4 ★★** 设备删除与点位删除的语义差异？点位删除为什么带 DeviceId 双条件？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteDeviceRepository.cs:44`；`SqlitePointRepository.cs:71`。

**Q7.5 ★★★** GetAllAsync 全量加载 + Include 的问题？DeviceSnapshotCache（TTL 10s）与失效策略解决了什么？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteDeviceRepository.cs:73`；ADR-002 P2-2。

---

## 八、后台任务与数据生命周期

**Q8.1 ★★** MeasurementRetentionService 的参数与默认值？执行流程？测试怎么注入小间隔？
代码定位：`src/NitroGateway.Persistence/Sqlite/MeasurementRetentionService.cs:34`；`SqliteServiceCollectionExtensions.cs:25` 注册处。

**Q8.2 ★★★** 保留清理失败「只记日志」vs 迁移备份失败「阻断启动」——可降级与不可降级的区分标准？
代码定位：`src/NitroGateway.Persistence/Sqlite/MeasurementRetentionService.cs`（PurgeOnceAsync）；`MigrationRunner.cs:63`。

**Q8.3 ★★★** PurgeAsync 的单条大 DELETE 有什么性能风险？改进方向？（SQLite 无原生分区，你会怎么做？）
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:267`。

---

## 九、可观测性与测试

**Q9.1 ★★** 时序写入的 Activity 追踪：span 名、tags、成功/失败状态？查询路径为什么没有 span（可观察性缺口）？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:33`；`GatewayActivities.SqliteWrite`。

**Q9.2 ★★** persistence 相关的测试文件与各自覆盖点？
代码定位：`tests/NitroGateway.UnitTests`（SqliteMeasurementStoreTests / SqliteForwardOutboxTests / SqliteErrorClassifierTests / SqliteAlarmRepositoryTests / MeasurementRetentionServiceTests / MeasurementWriteHostTests）。

**Q9.3 ★★★** 像「启动恢复」「重试超限丢弃」「分页夹紧」这类行为，测试用真 SQLite 还是 mock？红绿对照怎么用？并发类行为怎么测才稳定？
代码定位：`tests/NitroGateway.UnitTests/SqliteForwardOutboxTests.cs`、`SqliteMeasurementStoreTests.cs`。

---

## 十、扩展与设计权衡

**Q10.1 ★★★** 换 PostgreSQL：接口层动吗？实现层哪些是 SQLite 特定（时间字符串、UPSERT、wal_checkpoint、Dapper SQL）？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqlitePragmas.cs`、`MigrationRunner.cs`、`SqliteMeasurementStore.cs`。

**Q10.2 ★★★** MQTT 断网 24h：数据可靠性链路怎么走？主要风险是什么（buffer 表增长 vs measurements 保留）？
代码定位：`src/NitroGateway.Persistence/Sqlite/SqliteForwardOutbox.cs` 全链路；`MeasurementRetentionService.cs`。

**Q10.3 ★★★** measurements 大表优化方向（分区/降采样/索引/保留窗口）？各自代价？
代码定位：`src/NitroGateway.Persistence/Migrations/M001_CreateMeasurementsTable.cs`；`SqliteMeasurementStore.cs`。

**Q10.4 ★★★** 写放大评估：1s 采集 × N 点位的写入量？MeasurementWriteHost 的有界 Channel（1000, DropOldest）与批量 INSERT 的关系？「云转发可靠 vs 本地历史尽力而为」的分级依据？
代码定位：`src/NitroGateway.Collection/Dispatcher/MeasurementWriteHost.cs`（跨模块）；`src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs:33`。

**Q10.5 ★★★** 「接口只增不删」是绝对的吗？真想删掉 `Count` 同步属性，安全的演进路径是什么？
代码定位：`src/NitroGateway.Storage/Buffer/IForwardBuffer.cs:11`。
