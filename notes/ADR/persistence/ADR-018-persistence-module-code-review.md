# ADR-018: Persistence 模块 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施

## Context

Persistence 全量 code review 发现：保留清理锁库+静默丢数、仓储异常不归类、forward_buffer 无上限+死信无清理、Commit 无守卫、latest 同 timestamp 多行、启动恢复阻塞首解析、连接串解析不兼容变体。

## Decision

- D1 保留清理分批：SqliteMeasurementStore.PurgeAsync 分批删除（SELECT id LIMIT 1 万 → 按 id 批量删，每批独立事务），批间让出写锁窗口；新增迁移 M007 补 timestamp 单列索引，避免每批全表扫描（本 SQLite 编译版不支持 DELETE ... LIMIT）。
- D2 写失败可见：MeasurementWriteHost.ExecuteAsync 检查 WriteAsync 返回的 OperationResult，失败记 Error + 新增指标 nitro_store_write_failures_total，不再静默丢弃。
- D3 仓储异常归类：SqliteDeviceRepository/SqlitePointRepository 全部方法 catch + SqliteErrorClassifier 归类返回（对齐 Alarm 仓储）；DeviceManager.UnregisterAsync/UpdateStatusAsync 不再忽略 Delete/Save 返回值。
- D4 forward_buffer 上限 + 死信清理：SqliteForwardBuffer 新增 maxPending 入队上限（默认 10 万，超限拒绝入队 + Error 告警，配置 Persistence:ForwardBufferMaxPending）；接口新增 IForwardBuffer.PurgeDeadLettersAsync（只增不删）；新增 DeadLetterRetentionService 后台任务（默认保留 30 天/24h，配置 Persistence:DeadLetterRetentionDays/Interval）。
- D5 Commit 状态守卫：DELETE ... WHERE id IN @ids AND status='InFlight'，stale commit 不再误删未发送数据。
- D6 QueryLatestAsync pointId=null 改 ROW_NUMBER() PARTITION BY point_id 取每点最新，同 timestamp 不再返回多行。
- D7 SqlitePragmas 用静态 ConcurrentDictionary 缓存"库级 WAL 已确认"，热路径每操作省一次往返。
- D8 DomainMapper/SqliteAlarmRepository 枚举解析改 TryParse 回退默认值（脏数据不致配置读取整体失败）。
- D9 SqliteForwardBuffer 启动恢复（InFlight→Pending）移出构造器：延迟到首次使用异步完成（EnsureRecoveredAsync 闸门），DI 首解析不再被 DB 锁阻塞。
- D10 MigrationRunner.ExtractDataSource 改 SqliteConnectionStringBuilder 解析，兼容变体连接串。

## Rationale

- 分批清理让出写锁、索引避免全表扫描；写失败可见化；仓储统一异常契约；缓冲上限+死信保留防无限增长；Commit 守卫防误删；启动恢复延迟避免首解析阻塞。

## Consequences

- 保留清理不锁库不静默丢数；写失败可告警；forward_buffer 有界、死信自动清理；并发/脏数据/连接串变体场景更健壮。
