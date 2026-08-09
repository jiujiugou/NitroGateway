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

## 待处理条目（P3）

- P3-1 IForwardBuffer.Count 遗留债：同步查库属性（ADR-001 P3-13），生产消费方已全改 GetCountAsync（Forwarder.cs / ForwarderEngine.cs / StatusController.cs），仅测试仍用 Count；接口注释未提示阻塞风险。修复方向：接口注释补「同步查询可能阻塞，优先 GetCountAsync」。
- P3-2 IMeasurementStore.WriteAsync 失败语义未定义：注释只说「内部应做批量优化」，未承诺原子性；实现侧单事务全成功/回滚全失败（SqliteMeasurementStore.WriteAsync），消费方 MeasurementWriteHost 忽略返回值（MeasurementWriteHost.cs:65，实现侧问题见 ADR-018 P2-1）。修复方向：接口注释明确「单事务，全成功或全失败，调用方必须处理 Failure」。
- P3-3 QueryLatestAsync 无唯一性契约：实现按 MAX(timestamp) join（SqliteMeasurementStore.QueryLatestAsync），同时间戳多行会重复返回；MeasurementsController.LatestBatch 用 GroupBy 兜底（MeasurementsController.cs:50）——契约漏洞被消费方补丁掩盖。修复方向：接口注释承诺「每点最多一条」，实现按写入序去重。
- P3-4 DeleteAsync 级联语义未定义：IDeviceRepository.DeleteAsync / IPointRepository.DeleteAsync 注释仅「删除指定设备/点位」，设备删除时点位、测量数据是否级联清理未定义（DeviceManager 无说明）。修复方向：接口注释定义级联策略（或明确由调用方负责）。
- P3-5 GetByStatusAsync 状态来源语义不清：注释「按通信状态筛选设备」，但 DeviceManager.GetByStatusAsync 直通 repository（配置缓存 Status 列，DeviceManager.cs:70-72），StatusController.SystemStatus 用它统计在线数（StatusController.cs:64）——与 DeviceSummary 的 HealthMonitor 实时快照口径不一致，离线统计可能失真。修复方向：接口注释明确「状态指配置/最近一次持久化状态」，或 SystemStatus 改读 HealthMonitor 快照。
- P3-6 DeadLetterEntry.EnqueuedAt 语义：实现取 enqueued_at（原始入队时间，SqliteForwardBuffer.GetDeadLettersAsync），转死信时刻无字段；「入队时间」易被误读为「进死信时间」。修复方向：注释明确为原始入队时间；如需转死信时间后续加列（迁移）。

## 亮点

- ADR-005 修复到位：SaveBatchAsync 单事务、GetByIdAsync 文档对齐、QueryPagedAsync 落地、DeadLetterEntry 最小字段决策
- IForwardBuffer 状态机（Pending→InFlight→删除/死信）+ 启动恢复 InFlight 是数据可靠性骨架（ADR-001 已修）
- 接口只增不删纪律执行严格：Count / QueryByDeviceAsync 保留但生产消费方已切换
- OperationResult 统一返回、异常不抛出契约贯穿全部接口
