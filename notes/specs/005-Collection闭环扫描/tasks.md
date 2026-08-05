# [005] Collection 闭环补全（tasks — P0 批次）

## 前置

- [x] 决策点 1 = 方案 A（P0 优先分批, 用户 2026-08-05 拍板）
- [x] 决策点 2 = 快照携带点名称（G2 默认通过, 已按推荐实施, 用户可反对）

## 任务（P0 批次）

- [x] T-1 P0-1 MeasurementWriteHost 写入异常隔离 + 单测（→ 工程基线 错误处理）
- [x] T-2 P0-2 PointName 传递: PointSnapshot 加字段 + Pipeline 填充 + Dispatcher 透传 + 测试（→ 云端点名非空）
- [x] T-3 P0-3 CollectionEngine 异常恢复 + 托管服务测试（→ 工程基线 错误处理）

## 执行记录

- 2026-08-05: 扫描完成（见 spec.md）; P0 批次启动
- 2026-08-05 T-1: `MeasurementWriteHost.ExecuteAsync` 写入包 try/catch（OperationCanceledException 除外, 异常 LogError 后跳过本批继续消费, 服务不崩）; 新增 `MeasurementWriteHostTests`（FlakyStore 首写抛错 → 第二批仍写入）
- 2026-08-05 T-2: `PointSnapshot` 加 `string? PointName`; `PointValuePipeline` 4 处快照构造填 `point.Name`; `DataDispatcher` 改 `PointName = s.PointName ?? string.Empty`; 集成测试断言透传至 MeasurementRecord; 单测新增 `PointName_PropagatedToSnapshot`
- 2026-08-05 T-3: `CollectionEngine` 外层 catch 移入循环内: 异常 LogError + 延迟 5s（构造参数 `errorRetryDelay` 可覆盖, 默认 5s）后继续下一轮, 不再整机退出; 新增 `CollectionEngineTests`（首轮抛错 → 次轮仍采集, 放集成测试工程）
- 2026-08-05 验证: `dotnet build NitroGateway.slnx` → 0 错; `dotnet test NitroGateway.slnx` → 单测 117 + 集成 3 = 120 全绿（基线 117）
- 残留观察（P2 批次再处理）: `SqliteMeasurementStore` 查询重建 PointSnapshot 时表结构无 point_name 列, 回读 PointName 为 null; P0 不动库表

## 验收

- [x] 验收指标.md 已生成并勾选（P0 批次, 见本目录验收指标.md）
- [x] 全量测试通过（dotnet test: 单测 117 + 集成 3 = 120）
- [x] 三处 P0 修复各有测试锚定（T-1/T-3 新增测试, T-2 单测+集成断言）
- [x] 执行记录回填; 决策点 2 状态更新（G2 默认通过, 可反对）