# ADR-005: Storage 模块（纯接口层）优化清单

- 日期: 2026-08-07
- 状态: 全部条目已修复（2026-08-07）
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: src/NitroGateway.Storage 全部接口（Buffer/Configuration/TimeSeries）；纪律：接口只增不删；实现侧问题已在 ADR-001/002 登记的不重复开条目

## 引用已登记项（不重复）

- IMeasurementStore 缺最新值查询：Latest/LatestBatch 拉 1 小时全量内存过滤 → ADR-002 P2-4（修复方向：接口新增 QueryLatestAsync）
- IForwardBuffer.Count 同步属性：接口层迫使实现同步查 DB（每次开连接 ExecuteScalar）→ ADR-001 P3-13

## 已修复（2026-08-07）

- P2-1 IPointRepository 新增 SaveBatchAsync（单事务 upsert）；PointManager.ImportAsync 批量优先、失败回退逐条保留诊断；SqlitePointRepository.SaveBatchAsync 实现
- P3-1 IDeviceRepository.GetByIdAsync 文档改为「不存在返回 Failure」，与实现一致
- P3-2 DeadLetterEntry 注释说明仅含最小字段（设备名有意不冗余）
- P2-2 MeasurementsController.History 增加 limit（默认 1000）/offset 查询参数并改调 QueryPagedAsync（实现侧 LIMIT/OFFSET 夹紧 1..1000，已有单测覆盖）；默认 1000 与旧全量行为接近，客户端可显式分页
