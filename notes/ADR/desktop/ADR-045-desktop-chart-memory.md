# ADR-045: 桌面实时图表内存与主线程开销定位与修复（2026-08-15）
- 日期: 2026-08-15 | 状态: 已实施 | 来源: 用户反馈「桌面 UI 绘图吃内存很大」+ dotnet-stack 定位
- 关联: ADR-026（桌面壳）、ADR-037（S9/S12 曲线）、ADR-027（桌面评审）、ADR-042（性能优先级）

## 问题
- 现象：桌面采集端运行后内存持续偏高；主线程被 LiveCharts2 的 SkiaSharp 全量重绘占满
  （dotnet-stack：`SKElement.OnRender → CoreMotionCanvas.DrawFrame → VectorGeometry.Draw → SKCanvas.DrawPath`）。
- 根因：
  - 唯一图表：实时页 `RealtimeView.xaml:106` 的 `CartesianChart`，系列 `_series`（LineSeries，7200 点）整量交给
    SkiaSharp 画，绘制成本 ∝ 点数。
  - 常驻更新：`RealtimeViewModel.cs:109` `FrameReady += OnFrame` 进程级常驻；`OnFrame(:228)` 每 200ms 帧
    （`EventBridge.cs:46`）`_ui.Post` 后 `ChartValues.Add` + `TrimFront`（`Common.cs:25` 单次 Reset → LiveCharts 全量重建 7200 点）。
  - 后台照跑：`MainViewModel.cs:58` 切页只换 `CurrentViewModel`、不暂停实时页；`MainWindow` 未处理最小化
    → 不可见时重绘链照跑。
  - 动画未关：`_series` 构造（:73-79）未设 `AnimationsSpeed`，每帧产生逐点动画对象。
  - 已知库问题：LiveCharts2 WPF 切 Visibility/反复切页内存不回收（GitHub #1468/#737），7200 点接近官方 ~10k 上限（#1237）。

## 实施结果（组合方案，2026-08-15 完成）
1. 生命周期 IsActive：导航切走/窗口最小化 → `OnFrame` 直接 return + `_series.Values = null`/`ChartValues.Clear()`，
   回来重载最近窗口；缓解切页泄漏。
   - `RealtimeViewModel.IsActive`（ObservableProperty）；`MainViewModel.OnSelectedNavChanged` 切页时仅实时页置 true，
     `MainViewModel.SetRealtimeVisible` + `MainWindow.StateChanged` 处理窗口最小化/还原。
2. 图表只绑降采样小窗口：VM 保留 2h/7200 原始缓冲，给 LiveCharts 的集合用 min/max 分桶（或 LTTB）降到 ~1000 点，
   TrimFront 批量裁剪。
   - 原始缓冲 `_rawValues`（普通 `List<DateTimePoint>` 无集合通知，上限 `MaxChartPoints=7200`，溢出批量 `RemoveRange`）；
     显示集合 `ChartValues`（`RingObservableCollection`）由 `RefreshChart()` 用 `DownsampleMinMax`（min/max 分桶，
     保首末点+尖峰）降采样到 `ChartWindowPoints=1000` 后单次 `Replace`（`Common.cs` 新增，单次 Reset 通知）。
3. 关动画：`AnimationsSpeed = TimeSpan.Zero`。
   - `_series` 构造已设 `AnimationsSpeed = TimeSpan.Zero`。
4. 固定刷新：仅 `IsActive` 时 500ms 定时器从缓冲取快照更新，代替逐帧直推。
   - `OnFrame` 仍帧级追加原始缓冲（零重绘），每 500ms 节流触发一次 `RefreshChart()`（`_lastChartRefreshUtc` 节流）；
     历史预载 `LoadPointHistoryAsync` 完成立即 `RefreshChart()` 不等节流。
5. 不新增全局缓存层：EventBridge 已 200ms 合并（设备频率≠UI 频率），2h 历史由 DB 重载，避免第二份内存副本。

### 验证
- `dotnet build NitroGateway.slnx` 0 错误（34 警告均为既有 NU1903/NU1701/xUnit 分析器，与本次改动无关）。
- `dotnet test tests/NitroGateway.UnitTests --no-build` 593/593 通过（新增：原始缓冲 7200 窗口+降采样、
  DownsampleMinMax 保尖峰/首末点、IsActive 停用清空/停用不追加/激活重载）。
- 测试见 `tests/NitroGateway.UnitTests/RealtimeViewModelTests.cs`；改动文件 `RealtimeViewModel.cs` / `Common.cs` /
  `MainViewModel.cs` / `MainWindow.xaml.cs`。git 提交由用户执行。

## G1（行为变更）
- 曲线由「原始 7200 点逐点」变「2h 降采样 ~1000 点显示」（视觉近似，不丢形状）；切走/最小化暂停、回来重载；
  关闭动画。采集/存储/转发不动。
