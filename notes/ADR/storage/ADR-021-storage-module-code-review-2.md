# ADR-021: Storage 模块二轮 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施

## Context

Storage 纯接口层二轮 review 发现：查询接口缺上限/分页契约、遗留接口无消费方、接口注释与实现语义不一致。纪律：接口只增不删；实现侧已登记 ADR-001/002/018 的不重复。

## Decision

- D1 遗留接口标注：IMeasurementStore.QueryAsync/QueryByDeviceAsync 注释标注「无上限全量查询，生产已无调用方，遗留接口保留（接口只增不删），勿新增消费方；大结果集走 QueryPagedAsync/QueryLatestAsync」。
- D2 全量加载标注：IDeviceRepository.GetAllAsync/GetByStatusAsync、IPointRepository.GetByDeviceAsync 注释标注「全量加载无分页，规模增长时新增分页重载」。
- D3 IForwardBuffer.Count 注释补「同步查询可能阻塞，async 路径请用 GetCountAsync」。
- D4 IMeasurementStore.WriteAsync 注释承诺「单事务，全成功或全失败，调用方必须处理 Failure」。
- D5 QueryLatestAsync 注释承诺「每点最多一条」（同时间戳按写入序取最新）。
- D6 删除契约：IDeviceRepository.DeleteAsync 注释定义级联契约（EF Cascade 删点位，不动 measurements 时序表）；IPointRepository.DeleteAsync 注释明确仅删点位配置。
- D7 GetByStatusAsync 注释明确状态口径（配置/最近一次持久化状态，非 HealthMonitor 实时快照）。
- D8 DeadLetterEntry.EnqueuedAt 注释明确为原始入队时间（非转死信时刻）。

## Alternatives

- D1 备选：直接删除无消费方接口（违反接口只增不删纪律，且破坏未来兼容）。

## Rationale

- 接口只增不删保证契约稳定；注释明确语义防误用；分页/异步契约引导正确消费方式。

## Consequences

- Storage 接口文档与实现一致；遗留接口保留但不再新增消费方；大结果集消费方被引导到分页/latest 路径。
