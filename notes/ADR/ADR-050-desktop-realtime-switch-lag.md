# ADR-050: 桌面实时页切换设备卡——全历史最新值扫描（2026-08-16）

- 日期: 2026-08-16 | 状态: 已实施 | 来源: 用户反馈「实时设备绘图在切换和整体上讲，感觉还是有些卡」
- 关联: ADR-045（图表内存/主线程）、ADR-047（SQLite 查询移出 UI 线程）、ADR-042（性能优先级）

## 问题
- 现象：ADR-045/047 修复后，切换设备/点位仍可感知卡顿（网格空等、整体不够跟手）。
- 根因：切换设备时 `RealtimeViewModel.LoadPointsAsync` 仍执行
  `_store.QueryLatestAsync(deviceId, pointId: null)`——SQL 为
  `ROW_NUMBER() OVER (PARTITION BY point_id ORDER BY timestamp DESC)` 对该设备**全部历史**
  （30 天保留 × 点位数）做窗口扫描取每点最新值。虽已包 `Task.Run` 移出 UI 线程（ADR-047），
  但网格要等查询返回才填充，随表增长查询耗时线性变慢 → 用户感知「切设备卡」。
- 代码位置: `src/NitroGateway.Desktop/ViewModels/RealtimeViewModel.cs`
  （`LoadPointsAsync` 内 `QueryLatestAsync` 调用）、
  `src/NitroGateway.Persistence/Sqlite/SqliteMeasurementStore.cs`
  （`QueryLatestAsync` 的 ROW_NUMBER 全历史窗口）。
- 次要开销: 网格重建逐条 `Add`（N 次 CollectionChanged 通知），大点位设备切换时拖慢整体。

## 修复方向
- 帧内存最新值缓存：`RealtimeViewModel` 新增 `Dictionary<Guid, PointSnapshot> _latestByPoint`，
  `OnFrame` 对每帧所有测量点以 O(1) 写入（无 UI 通知、不随切设备清空）。
- 切设备即时填充：`LoadPointsAsync` 先用「配置 + `_latestByPoint`」立即重建网格
  （`Points` 改 `RingObservableCollection` + 单次 `Replace`），不再等 DB。
- DB 兜底降级：仅当所选设备存在从未在帧中出现过的点位（冷启动/离线）时，后台跑一次
  `QueryLatestAsync` 填充缺失；结果只填仍缺失的点位（帧数据更新鲜，以帧为准，不覆盖）。
- 验证：RealtimeViewModelTests 增「帧驱动切设备不触发 QueryLatestAsync、网格即时有值」
  与「缺失点位 DB 兜底填充且不覆盖帧值」；build + 全量单测红绿对照。

## G1（行为变更）
- 在线设备切换不再等待全历史最新值查询；冷启动/离线设备由后台 DB 兜底填充（视觉不空等）。
- 采集/存储/转发链路不动。

## 实施（2026-08-16）
- 改动（仅 `src/NitroGateway.Desktop`，未升级/降级依赖包）：
  - `ViewModels/RealtimeViewModel.cs` 新增 `Dictionary<Guid, PointSnapshot> _latestByPoint`：
    `OnFrame` 对每帧所有测量点以 O(1) 写入内存最新值快照（覆盖全部设备、不随切设备清空）。
  - `LoadPointsAsync` 改为两段桩注：
    ① 先用「配置 + `_latestByPoint`」经 `Points.Replace`（`RingObservableCollection`，单次 Reset）
       立即填充网格，不再等 DB；
    ② 仅当存在从未在帧中出现过的点位（冷启动/离线）时，才 `Task.Run` 跑一次
       `QueryLatestAsync` 兜底，结果只填仍缺失的点位（帧数据更新鲜，以帧为准、不覆盖）。
  - `OnSelectedDeviceChanged`：取消选中才立即清空网格；切设备时把「清空」延后到阶段①
    `Replace` 一次重建（避免 Clear + Replace 两次整表通知），大点位设备切换更快。
  - `Points` 由 `ObservableCollection` 改为 `RingObservableCollection`（`Replace` 取代逐条 `Add`）。
- 测试（`tests/NitroGateway.UnitTests`）：
  - `DesktopViewModelTestHelpers.StagedMeasurementStore` 新增 `LatestDequeueCount`
    （`Interlocked` 出队计数，供断言「未触发 DB 最新值查询」）。
  - 新增 `Frame_driven_device_switch_does_not_query_latest_and_grid_is_immediate`
    （先发帧预热内存，连续切三次设备，`LatestDequeueCount == 0` 且网格即时显示帧值）与
    `Missing_point_falls_back_to_db_and_does_not_override_frame_value`
    （在线点位由帧值 10 显示、DB 旧值 99 不覆盖；离线点位由 DB 兜底填充 20，兜底仅 1 次）。
- 验证：`dotnet build NitroGateway.slnx` 0 错误（警告均为既有 NU1701/xUnit 分析器）；
  `dotnet test tests/NitroGateway.UnitTests --no-build` 606/606 通过（基线 604 + 2 新增）。
  未提交（git 由用户执行）。
