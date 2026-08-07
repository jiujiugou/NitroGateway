# ADR-002: Persistence 模块优化清单

- 日期: 2026-08-07
- 状态: 全部条目已处理（2026-08-07）——P1 已修复；P2-1/P2-2/P2-4、P3-1~P3-4 已修复
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除本清单对应条目
- 范围: src/NitroGateway.Persistence 全模块（Sqlite 实现 + EF 仓储 + FluentMigrator）

## 处理记录（2026-08-07）

- P2-1 WAL/PRAGMA：新增 `SqlitePragmas.Apply`（journal_mode=WAL + synchronous=NORMAL + busy_timeout=5000），MigrationRunner 启动时应用（WAL 为库级持久设置），SqliteMeasurementStore/SqliteForwardBuffer 每连接打开后应用
- P2-2 采集热路径全量加载：新增 `IDeviceSnapshotCache`/`DeviceSnapshotCache`（Singleton，TTL 10s 兜底）；DeviceManager.GetAllAsync 走缓存，注册/注销/状态变更/点位增删改（含批量部分成功）均 Invalidate；与 docs/02-架构理解.md 的 DeviceCache 设计一致
- P2-2 方案 1（状态与配置解耦）：缓存只保证「设备+点位配置」低频读取；运行状态以 `IDeviceHealthMonitor` 实时快照为准——DeviceCollector 维护过滤、StatusController.DeviceSummary 均改读 HealthMonitor（无 HealthMonitor 快照的历史设备回退配置 Status）；新增 DeviceCollectorMaintenanceTests 3 个（HealthMonitor Maintenance 跳过 / HealthMonitor Online 强制采集 / 无快照回退）
- P2-4 latest SQL 化：IMeasurementStore 新增 `QueryLatestAsync`（接口只增不删）；pointId 非空取 LIMIT 1 最新，null 按 point_id 分组取每点最新；MeasurementsController.Latest/LatestBatch 改调，不再拉 1 小时全量内存过滤；新增 2 个单测
- P3-1 M003 命名风格：已执行迁移不动（改列名破坏既有库），类注释说明历史命名决策
- P3-2 MigrationRunner 日志：DatabaseInitializationExtensions 从 DI 取 ILoggerFactory 传入
- P3-3 备份一致性：备份前先 `PRAGMA wal_checkpoint(TRUNCATE)` 再复制；首次运行（库不存在）不产生空备份
- P3-4 SqliteErrorClassifier 语义：IOERR(10)/CORRUPT(11) 改归 Storage（原误标 StorageFull），仅 SQLITE_FULL(13) 表示磁盘满
- 验证: build 0 错；UnitTests 171 通过（上轮 168 + 新增 3）；IntegrationTests 12 通过
