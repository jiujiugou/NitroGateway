# ADR-002: Persistence 模块优化决策

- 日期: 2026-08-07 | 状态: 已实施

## Context

Persistence 采集热路径存在多处低效与语义问题：PRAGMA 缺失（journal_mode 默认非 WAL）、每轮全量加载设备/点位、Latest/LatestBatch 拉 1 小时全量内存过滤、SQLite 错误分类不准。

## Decision

- D1 WAL/PRAGMA：SqlitePragmas.Apply 应用 journal_mode=WAL + synchronous=NORMAL + busy_timeout=5000；MigrationRunner 启动时应用（WAL 为库级持久设置），各数据连接打开后应用。
- D2 配置缓存：新增 IDeviceSnapshotCache/DeviceSnapshotCache（Singleton，TTL 10s 兜底）；设备+点位配置低频读取走缓存，注册/注销/状态变更/点位增删改（含批量部分成功）均失效。
- D3 状态与配置解耦：缓存只保证配置读取；运行状态以 IDeviceHealthMonitor 实时快照为准（无快照的历史设备回退配置 Status）。
- D4 latest SQL 化：IMeasurementStore 新增 QueryLatestAsync（每点最新）；pointId 非空取 LIMIT 1，null 按 point_id 分组取每点最新；Latest/LatestBatch 改调，不再拉 1 小时全量内存过滤。
- D5 错误分类：IOERR(10)/CORRUPT(11) 归 Storage（原误标 StorageFull），仅 SQLITE_FULL(13) 表示磁盘满。
- D6 M003 历史迁移命名风格不改（改列名破坏既有库），类注释说明历史命名决策。

## Alternatives

- D3 备选：把状态并入缓存（单源，但状态高频变化会让配置缓存频繁失效）。
- D4 备选：维持 1 小时全量内存过滤（实现简单，但随保留期线性变慢）。

## Rationale

- WAL 提升并发读写；配置缓存命中低频读取、失效点明确；状态实时走 HealthMonitor 保证在线判定准确；latest 用 SQL 收敛扫描范围；错误分类修正避免磁盘满误报。

## Consequences

- 热路径不再全量加载配置、latest 查询收敛为索引扫描；配置变更即时失效缓存（≤TTL 兜底）；错误分类更准确，DiskGuard 与 SQLITE_FULL 兜底语义不变。
