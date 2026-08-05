# [005] Collection 闭环补全（plan — 怎么做）

## 现状（相关代码位置）

- 缺口清单见 spec.md; P0 三处: `Dispatcher/MeasurementWriteHost.cs`、`Dispatcher/DataDispatcher.cs`、`CollectionEngine.cs`
- 相关领域对象: `Domain/Devices/PointSnapshot.cs`（注释已声明自描述含点位名称, 但字段缺失）

## 方案

- 分批: P0（本次）→ P1（可观测/契约）→ P2（文档）→ P3（测试补缺）
- P0-1 MeasurementWriteHost: 写入异常 try/catch + 日志 + 继续消费, 不崩服务
- P0-2 PointName: `PointSnapshot` 增加 `PointName` 字段（加法变更, 可空）, Pipeline 填充, Dispatcher 透传
- P0-3 CollectionEngine: 外层异常改为记录日志 + 5 秒后重启采集循环, 不再永久退出

## 风险与对策

- 风险: PointSnapshot 加字段影响 Alarm/Webapi 等消费方 → 对策: 加法变更（可空字段）, 全量测试验证
- 风险: 引擎重启循环日志刷屏 → 对策: 仅异常时打 Error, 5 秒间隔防抖

## 假设（需用户确认）

- [ ] 决策点 2 采用推荐: 快照携带点名称（用户未答, 采用推荐, 可反对）

## 确认状态

- [ ] 闸门级别: G2（单模块缺陷修复, 默认通过）; 决策点 1 已拍板 = 方案 A（P0 优先分批）
