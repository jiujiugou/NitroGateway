# ADR-047: 桌面切换卡顿根因——SQLite 查询阻塞 UI 线程

- 日期: 2026-08-15 | 状态: 已实施
- 来源: 用户反馈「LiveCharts2 性能不行，切换窗口/设备点位卡顿」
- 关联: ADR-045（图表内存与主线程开销，已完成）；ADR-026（UI 帧桥）

## Context

实时图表页在切换窗口/设备/点位时明显卡顿，7×24 长时间运行后加剧。根因：Microsoft.Data.Sqlite 的 async（OpenAsync/Dapper QueryAsync）实为「同步外包」——在调用线程上同步跑完才返回已完成 Task，await 不让出线程池。RealtimeViewModel 三处查询均从 UI 线程事件回调发起：LoadPointsAsync 的 QueryLatestAsync（ROW_NUMBER 分区扫全设备历史，随 30 天保留数据量线性变慢）、LoadPointHistoryAsync 的 QueryPagedAsync（2h 窗口）、缓存失效时的 DB 查询。实测 120 万行分区查询在调用线程同步阻塞约 3.8s；Asynchronous=True 在 Microsoft.Data.Sqlite 10.x 已移除，连接串方案不可行。

## Decision

- D1 将 store/缓存查询移出 UI 线程：ViewModel 内 await Task.Run(() => _store.XXXAsync(...))；SqliteMeasurementStore 每次调用新建连接、无线程亲和问题，最小改动即可。
- D2 不采用连接串 Asynchronous=True 方案：Microsoft.Data.Sqlite 10.x 已移除该关键字（抛「keyword 'asynchronous' is not supported」）。
- D3 可选优化（后续）：① DataGrid 每帧刷新节流（500ms~1s）；② 实时表格最新值改由帧内存快照维护，不再每次切设备查 DB（应对 7×24 表增长）。

## Alternatives

- 连接串 Asynchronous=True：10.x 已移除，不可行。
- 换真异步存储层：改动大，超出问题范围。

## Rationale

Task.Run 在 ViewModel 侧移出 UI 线程是当前存储实现下的最小改动；SQLite async 是同步外包，必须在调用方包 Task.Run 才能让出 UI 线程。

## Consequences

- 查询异步化，UI 不再因查询冻结；切换设备/点位/窗口不再卡顿。
- 采集/存储/转发逻辑不动。
