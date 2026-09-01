# ADR-005: Storage 模块（纯接口层）优化决策

- 日期: 2026-08-07 | 状态: 已实施

## Context

Storage 纯接口层存在接口契约缺口与文档不一致：批量导入无单事务接口、历史查询无分页、文档与实现不符、死信字段冗余。纪律：接口只增不删；实现侧问题已在 ADR-001/002 登记的在此不重复。

## Decision

- D1 IPointRepository 新增 SaveBatchAsync（单事务 upsert）；PointManager.ImportAsync 批量优先、失败回退逐条保留诊断。
- D2 MeasurementsController.History 增加 limit（默认 1000）/offset 查询参数并改调 QueryPagedAsync；客户端可显式分页。
- D3 IDeviceRepository.GetByIdAsync 文档明确「不存在返回 Failure」，与实现一致。
- D4 DeadLetterEntry 仅含最小字段（设备名有意不冗余），注释说明。
- D5 引用已登记项不重复：IMeasurementStore 最新值 → 接口新增 QueryLatestAsync（ADR-002）；IForwardBuffer.Count 同步查库 → 接口新增 GetCountAsync（ADR-001）。

## Alternatives

- D2 备选：维持全量返回（客户端简单，但数据量大时传输与渲染压力大）。

## Rationale

- 批量 upsert 减少事务开销；分页收敛查询结果集；接口只增不删保证兼容；不重复登记已归口问题。

## Consequences

- 批量导入原子化；历史查询可控；接口文档与实现一致；Storage 层接口面保持稳定可演进。
