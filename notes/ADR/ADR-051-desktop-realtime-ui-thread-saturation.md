# ADR-051: 桌面实时页 UI 线程饱和——逐帧刷表 + 失焦未暂停（2026-08-16）

- 日期: 2026-08-16 | 状态: 已实施 | 来源: 用户反馈「切设备和点位卡、滚轮/点击卡、从其他窗口切回实时窗口卡、切回后还卡一阵」（10 设备，其中 1×500 点位 + 1×50 点位）
- 关联: ADR-045（图表内存/主线程）、ADR-047（SQLite 查询移出 UI 线程）、ADR-050（帧内存缓存切设备即时填充）

## 问题
- 现象：实时页「交互不跟手」——切设备/切点位卡、DataGrid 滚轮与点击卡、窗口切换卡且切回后仍卡一阵。
- 根因（ADR-045/047/050 之后仍残留）：
  1. 500 点位设备每 200ms 一帧，`OnFrame` 对每点逐个 `item.Update`（设 4 个 ObservableProperty：
     值/质量/时间/IsBad），跨 `_ui.Post` 在 UI 线程执行 → 约 500×4×5fps ≈ 1 万属性通知/秒，
     DataGrid 单元格刷新把 UI 线程压满；
  2. ComboBox 下拉/滚轮/点按与帧刷新同线程排队，被持续刷新饿死 → 交互不跟手；
  3. 窗口失焦未暂停：`MainWindow` 只处理 `StateChanged`（最小化），未处理 `Deactivated`（失焦），
     切走时后台仍全速刷；切回要追赶积压 + 整窗重绘 → 切换卡、切回后仍卡；
  4. 切设备/点位本身较重（500 行重建 + DataGrid layout + LiveCharts 直绘），叠加持续刷新背压更卡。
  5. 50 点位设备不卡 → 验证开销与点位数线性相关。
- 代码位置: `src/NitroGateway.Desktop/ViewModels/RealtimeViewModel.cs`（`OnFrame` 逐点 `item.Update`）、
  `src/NitroGateway.Desktop/Views/MainWindow.xaml.cs`（仅 `StateChanged`，缺 `Deactivated/Activated`）、
  `src/NitroGateway.Desktop/Views/RealtimeView.xaml`（DataGrid 未显式确认虚拟化）。

## 修复方向
1. 表格节流（治本）：把「值进内存」与「刷表格」解耦——每帧值仍 O(1) 入 `_latestByPoint`（无通知），
   DataGrid 行按节流周期（500ms~1s）批量刷一次；500 点位 ×4×5fps 的逐帧通知降为 ×2fps
   （约 1 万→4 千/秒，2.5~5 倍）；表格值最多滞后 ≤ 节流周期（监控无感）。`RefreshGridFromCache`
   供节流与恢复补齐复用。
2. 失焦暂停（补漏）：`MainWindow` 加 `Deactivated → SetRealtimeVisible(false)`、
   `Activated → SetRealtimeVisible(true)`（实时页 `OnIsActiveChanged(true)` 用内存缓存一次补齐表格）；
   直接解决「切窗口卡、切回还卡一阵」。最小化/还原边界用 `WindowState` 守卫避免激活序竞态
   （还原时可能 Activated 先于 StateChanged，避免在仍最小化时恢复刷新）。
3. DataGrid 显式开虚拟化：`EnableRowVirtualization/EnableColumnVirtualization/CanContentScroll`，
   确认 500 行只渲染可见行（`DataGridRowStyle` 模板用标准 `DataGridCellsPresenter`，不破坏虚拟化）。
4. 备选（先不做）：切设备/点位「先停后起」——步骤 1 落地实测后再定。

## G1（行为变更）
- 表格显示值最多滞后 ≤ 节流周期（默认 500ms，监控无感）；
- 窗口失焦后实时页后台停止刷新，切回时立即恢复并用内存缓存补齐表格（行为变更，已向用户说明）；
- 采集/存储/转发链路不动；曲线仍 500ms 固定刷新不变。

## 验证
- RealtimeViewModelTests 增：帧到达但未到节流周期表格不更新（缓存已更新）；到节流周期/恢复批量补齐；
  失焦暂停帧不更新；恢复补齐。build + 全量单测红绿对照（基线 606）。

## 实施（2026-08-16）
- 改动（仅 src/NitroGateway.Desktop + 单测，未升级/降级依赖包）：
  - `ViewModels/RealtimeViewModel.cs`：`OnFrame` 去掉逐点 `item.Update`（原 500×4×5fps ≈ 1 万通知/秒
    压满 UI 线程）；每帧值仍 O(1) 入 `_latestByPoint`（无通知），DataGrid 行按
    `GridRefreshInterval`（内部可调，默认 500ms）经 `RefreshGridFromCache` 批量刷
    （1 万→4 千通知/秒，2.5 倍）；`OnIsActiveChanged(true)` 恢复可见时用缓存一次补齐表格；
    暴露 `LatestByPoint` 只读视图供测试断言缓存已更新。
  - `Views/MainWindow.xaml.cs`：加 `Deactivated → SetRealtimeVisible(false)`（失焦暂停，后台不再
    全速刷）、`Activated → SetRealtimeVisible(true)`（恢复）；用 `WindowState == Minimized` 守卫，
    避免还原时 Activated 先于 StateChanged 在仍最小化时恢复刷新。
  - `Views/RealtimeView.xaml`：DataGrid 显式 `EnableRowVirtualization/EnableColumnVirtualization/
    CanContentScroll/VirtualizingPanel.IsVirtualizing/VirtualizationMode=Recycling`（模板为标准
    `DataGridCellsPresenter`，不破坏虚拟化；500 行只渲染可见行）。
- 测试（`tests/NitroGateway.UnitTests/RealtimeViewModelTests.cs` 新增 3 个）：
  `Frame_updates_cache_but_grid_refresh_is_throttled`（帧已到、缓存已更新、网格未逐帧刷）、
  `Grid_refreshes_from_cache_on_throttle_boundary_and_resume`（节流到点/失焦恢复批量补齐）、
  `Grid_refreshes_each_frame_when_throttle_disabled`（节流为 0 时每帧即刷，控制组验证正确性）。
- 验证：`dotnet build NitroGateway.slnx` 0 错误（28 警告均为既有 NU1701/xUnit 分析器）；
  `dotnet test tests/NitroGateway.UnitTests --no-build` 609/609 通过（基线 606 + 3 新增）。
  未提交（git 由用户执行，notes/ 本地保留）。
