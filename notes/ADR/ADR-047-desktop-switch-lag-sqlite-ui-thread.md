# ADR-047: 桌面切换卡顿根因——SQLite 查询阻塞 UI 线程（2026-08-15）

- 日期: 2026-08-15 | 状态: 已实施 | 来源: 用户反馈「LiveCharts2 性能不行，切换窗口/设备点位卡顿」
- 关联: ADR-045（图表内存与主线程开销，已完成）；ADR-026（UI 帧桥）；ADR-027（桌面代码评审）

## 问题
- 现象: 实时图表页（LiveCharts2）在**切换窗口、切换设备/点位**时明显卡顿，窗口要卡一下才出现；7×24 长时间运行后加剧。
- 根因: **SQLite 查询在 UI 线程上同步执行**，不是 LiveCharts2 本身。
  - Microsoft.Data.Sqlite 的 async（`OpenAsync`/Dapper `QueryAsync`）实为「同步外包」：在**调用线程**上同步跑完才返回已完成 Task，`await` 不让出线程池。
  - `RealtimeViewModel` 三处查询均从 UI 线程事件回调发起:
    - `OnSelectedDeviceChanged` → `LoadPointsAsync` → `_store.QueryLatestAsync(deviceId, pointId:null)`
      （`ROW_NUMBER() OVER (PARTITION BY point_id ORDER BY timestamp DESC)` 扫全设备历史，随 30 天保留数据量线性变慢）;
    - `OnSelectedPointChanged` / `OnIsActiveChanged(true)`（**切回实时页触发**）→ `LoadPointHistoryAsync` → `QueryPagedAsync`（2h 窗口）;
    - `LoadDevicesAsync`/`LoadPointsAsync` → `_cache.GetAllAsync()`（缓存失效时也是 DB 查询）。
- 代码位置: `src/NitroGateway.Desktop/ViewModels/RealtimeViewModel.cs`
  （`LoadPointsAsync`/`LoadPointHistoryAsync`/`LoadDevicesAsync`）、
  `src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs`（`QueryLatestAsync`/`QueryPagedAsync` 的 async 外壳）。
- 与 ADR-045 的关系: ADR-045 已解决 LiveCharts2 渲染/后台重绘/内存问题（IsActive、降采样、关动画）；
  本 ADR 是第二个根因——**查询本身阻塞 UI 线程**，切设备/切回实时页时暴露，故切卡仍在。

## 修复方向
- 将 store/缓存查询移出 UI 线程: ViewModel 内 `await Task.Run(() => _store.XXXAsync(...))`；
  `SqliteMeasurementStore` 每次调用新建连接，无线程亲和问题，最小改动即可。
- 可选优化（后续）: ① DataGrid 每帧刷新节流（500ms~1s，点位多时持续耗 UI 线程）；
  ② 实时表格最新值改由帧内存快照维护，不再每次切设备查 DB（应对 7×24 表增长）。
- 验证: 切换设备/点位/窗口不再卡；VS 性能探查确认 UI 线程无 SQLite 同步阻塞；build + 单测红绿对照。

## G1（行为变更）
- 查询异步化，UI 不再因查询冻结；采集/存储/转发逻辑不动。

## 实施（2026-08-15）
- 探测结论: ① 探测项目 120 万行 `ROW_NUMBER` 分区查询，`OpenAsync`+`QueryAsync` 在调用线程同步跑完
  （qStartThread==qEndThread==泵线程），阻塞约 3.8s，确认「同步外包」根因；② `Asynchronous=True` 在
  Microsoft.Data.Sqlite 10.x 已移除（抛「keyword 'asynchronous' is not supported」），连接串方案不可行，
  只能 ViewModel 侧 `Task.Run` 移出 UI 线程。
- 改动（`src/NitroGateway.Desktop`，未升级/降级依赖包）:
  - `ViewModels/RealtimeViewModel.cs`: `LoadPointsAsync` 的 `_store.QueryLatestAsync(deviceId, pointId:null)`
    与 `LoadPointHistoryAsync` 的 `_store.QueryPagedAsync(...)` 均包 `Task.Run`；后者先捕获
    `var deviceId = SelectedDevice.Id` 局部变量再查（await 期间不依赖可变属性）。
  - `ViewModels/HistoryViewModel.cs`: `QueryPageAsync` 先捕获 `deviceId`/`pointId` 局部变量，
    `_store.QueryPagedAsync(...)` 包 `Task.Run`。
  - `_cache.GetAllAsync()` 未包 Task.Run（内存热路径；冷路径为 EF Core 真异步，与 SQLite 同步外包不同）。
- 测试（`tests/NitroGateway.UnitTests`）: `DesktopViewModelTestHelpers.StagedMeasurementStore` 加 `_gate` 锁 +
  `Interlocked` 出队计数 `PagedDequeueCount`（供测试等待查询真正触达存储）；两个竞态测试（
  `RealtimeViewModelTests.LoadPointHistoryAsync_stale_point_result_does_not_override_new_point`、
  `HistoryViewModelTests.QueryAsync_is_not_reentrant_while_in_flight`）断言前等待
  `PagedDequeueCount >= 1`（出队移线程池后 FIFO 顺序不确定）。
- 验证: `dotnet build NitroGateway.slnx` 0 错误；`dotnet test tests/NitroGateway.UnitTests --no-build`
  602/602 通过。未提交（git 由用户执行）。
