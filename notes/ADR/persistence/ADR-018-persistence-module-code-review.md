# ADR-018: Persistence 模块 Code Review 清单

- 日期: 2026-08-09 | 状态: 全部条目已处理（2026-08-09）
- 用途: 对 Persistence 模块全量 code review 产出的问题清单；修复后在代码加注释并删除本条
- 范围: src/NitroGateway.Persistence 全部（Sqlite 实现 + EF 仓储 + FluentMigrator + 保留任务）+ 直接消费方 DeviceManager/PointManager
- 验证基线: `dotnet build` 0 错误；UnitTests 292 通过；IntegrationTests 40 通过（2026-08-09）

## 处理记录（2026-08-09）

### P2 可靠性
- P2-1 保留清理锁库 + 静默丢数：
  - `SqliteMeasurementStore.PurgeAsync` 改分批删除（SELECT id LIMIT 1 万 → 按 id 批量删，每批独立事务），
    批间让出写锁窗口；新增迁移 M007 补 timestamp 单列索引，避免每批全表扫描
    （注：本 SQLite 编译版不支持 DELETE ... LIMIT，代码注释已说明）
  - `MeasurementWriteHost.ExecuteAsync` 检查 WriteAsync 返回的 OperationResult，失败记 Error +
    新增指标 `nitro_store_write_failures_total`（NitroMetrics.StoreWriteFailures），不再静默丢弃
- P2-2 Device/Point 仓储异常不归类、manager 死分支：
  - `SqliteDeviceRepository` / `SqlitePointRepository` 全部方法 catch + SqliteErrorClassifier 归类返回（对齐 Alarm 仓储）
  - `DeviceManager.UnregisterAsync`/`UpdateStatusAsync` 不再忽略 Delete/Save 返回值
- P2-3 forward_buffer 无上限 + 死信无清理：
  - `SqliteForwardBuffer` 新增 `maxPending` 入队上限（默认 10 万，超限拒绝入队 + Error 告警，
    配置项 `Persistence:ForwardBufferMaxPending`）
  - 接口新增 `IForwardBuffer.PurgeDeadLettersAsync`（只增不删），实现按 enqueued_at 分批清理死信
  - 新增 `DeadLetterRetentionService` 后台任务（默认保留 30 天/24h，配置项 `Persistence:DeadLetterRetentionDays`/`Interval`）

### P3 可维护性
- P3-1 `CommitAsync` 加状态守卫：`DELETE ... WHERE id IN @ids AND status='InFlight'`，stale commit 不再误删未发送数据
- P3-2 `QueryLatestAsync` pointId=null 改 ROW_NUMBER() PARTITION BY point_id 取每点最新，同 timestamp 不再返回多行
- P3-3 `SqlitePragmas` 用静态 ConcurrentDictionary 缓存"库级 WAL 已确认"，热路径每操作省一次往返
- P3-4 `DomainMapper` 与 `SqliteAlarmRepository.ToDomain` 枚举解析改 TryParse 回退默认值（脏数据不致配置读取整体失败）
- P3-5 `SqliteForwardBuffer` 启动恢复（InFlight→Pending）移出构造器：延迟到首次使用异步完成
  （`EnsureRecoveredAsync` 闸门），DI 首解析不再被 DB 锁阻塞
- P3-6 `MigrationRunner.ExtractDataSource` 改 `SqliteConnectionStringBuilder` 解析，兼容变体连接串

## 覆盖缺口（已补测试）
- PurgeAsync 分批删除（小批量上限全删 + 保留边界）✓
- MeasurementWriteHost 对 WriteAsync 失败结果的检查与继续消费 ✓
- QueryLatestAsync 同 timestamp 重复行去重 ✓
- Device 仓储异常归类（NOT NULL 违反 → Storage 失败而非抛出；表缺失分类）✓
- DomainMapper 未知枚举回退 Unknown ✓
- Enqueue 满返回失败、Commit 状态守卫、死信保留清理 ✓
- DeadLetterRetentionService 周期/失败重试 ✓
- MigrationRunner 连接串变体解析 ✓
