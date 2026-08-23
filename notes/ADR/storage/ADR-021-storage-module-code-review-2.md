# ADR-021: Storage 模块二轮 Code Review 清单

- 日期: 2026-08-09 | 状态: P2×2 已修复（2026-08-09），P3×6 待处理
- 用途: Storage 纯接口层二轮 review 问题清单；修复后在代码加注释并删除本条
- 范围: src/NitroGateway.Storage 全部接口（Buffer / Configuration / TimeSeries）+ 实现侧关键行为核对（SqliteForwardBuffer / SqliteMeasurementStore）+ 消费方（Forwarder / DataDispatcher / MeasurementWriteHost / MeasurementsController / DeviceManager / PointManager）+ 相关测试
- 首轮 ADR-005 已修条目不重复；实现侧已登记 ADR-001/002/018 的不重复
- 纪律: 接口只增不删

## 修复记录（2026-08-09）

- P2-1 查询接口缺上限/分页契约：`IMeasurementStore.QueryAsync/QueryByDeviceAsync` 注释标注「无上限全量查询，生产已无调用方，遗留接口保留（接口只增不删），勿新增消费方；大结果集走 QueryPagedAsync/QueryLatestAsync」；`IDeviceRepository.GetAllAsync/GetByStatusAsync`、`IPointRepository.GetByDeviceAsync` 注释标注「全量加载无分页，规模增长时新增分页重载」。
- P2-2 QueryAsync/QueryByDeviceAsync 生产无调用方：接口注释标记遗留状态，防后续误用（MeasurementsController 全走 QueryPagedAsync/QueryLatestAsync，不改消费方）。
- 验证: 纯注释改动；build 0 错误；UnitTests 215；IntegrationTests 40（2026-08-09）

## 修复记录（2026-08-10，P3 全部修复，条目已清）

- P3-1 IForwardBuffer.Count 注释补「同步查询可能阻塞，async 路径请用 GetCountAsync」
- P3-2 IMeasurementStore.WriteAsync 注释承诺「单事务，全成功或全失败，调用方必须处理 Failure」
- P3-3 QueryLatestAsync 注释承诺「每点最多一条」（同时间戳按写入序取最新）
- P3-4 IDeviceRepository.DeleteAsync 注释定义级联契约（EF Cascade 删点位，不动 measurements 时序表）；IPointRepository.DeleteAsync 注释明确仅删点位配置
- P3-5 GetByStatusAsync 注释明确状态口径（配置/最近一次持久化状态，非 HealthMonitor 实时快照）
- P3-6 DeadLetterEntry.EnqueuedAt 注释明确为原始入队时间（非转死信时刻）
## 亮点

- ADR-005 修复到位：SaveBatchAsync 单事务、GetByIdAsync 文档对齐、QueryPagedAsync 落地、DeadLetterEntry 最小字段决策
- IForwardBuffer 状态机（Pending→InFlight→删除/死信）+ 启动恢复 InFlight 是数据可靠性骨架（ADR-001 已修）
- 接口只增不删纪律执行严格：Count / QueryByDeviceAsync 保留但生产消费方已切换
- OperationResult 统一返回、异常不抛出契约贯穿全部接口
