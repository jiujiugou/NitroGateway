# ADR-012: 磁盘保护 DiskGuard（Persistence + 联动）

- 日期: 2026-08-07 | 状态: 设计完成，待实现 | 用途: 7×24 无人值守下磁盘写满前预警并降级，保护 SQLite 与日志
- 范围: Storage（新 IDiskStatus 接口）、Persistence（DiskGuardService 实现）、Collection（DataDispatcher 暂停写入）、Forwarder（暂停出队）、Webapi（DiskHealthCheck + 配置）

## 设计
- P1 新接口 `IDiskStatus`（Storage，只增不删）: `DiskLevel Level { get; }`（Healthy/Warning/Critical）+ `event Action<DiskLevel>? Changed`
- P2 实现 `DiskGuardService`（Persistence，BackgroundService，默认 60s 周期）: 检查 `Persistence:ConnectionString` 的 Data Source 所在目录与 `logs/` 目录剩余空间；阈值 `Disk:WarningFreeBytes`（默认 1GB）、`Disk:CriticalFreeBytes`（默认 256MB）；恢复滞后 20% 防抖，避免临界抖动
- P3 联动: Critical → DataDispatcher 跳过 measurement 写入与 forward_buffer 入队（保留内存最新值缓存）、ForwarderEngine 跳过出队；Warning → 仅日志 + 指标 + 健康检查 Degraded
- P4 可观测: 新增 DiskHealthCheck（Critical→Unhealthy、Warning→Degraded）；Prometheus 增 `nitro_disk_free_bytes`
- P5 配置: appsettings 新增 `Disk` 段，默认值保证零配置可用

## 验证
- DiskGuardTests（阈值判断 / 滞后恢复 / 状态变化事件，纯逻辑可测）+ 全量测试通过

## 关联
- SQLITE_FULL 兜底分类已存在（ADR-002 P3-4）；DiskGuard 是第一道防线，不改变该兜底语义
