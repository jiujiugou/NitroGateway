# ADR-051: 桌面实时页 UI 线程饱和——逐帧刷表 + 失焦未暂停

- 日期: 2026-08-16 | 状态: 已实施
- 来源: 用户反馈「切设备和点位卡、滚轮/点击卡、从其他窗口切回实时窗口卡、切回后还卡一阵」（10 设备，其中 1×500 点位 + 1×50 点位）
- 关联: ADR-045（图表内存/主线程）、ADR-047（SQLite 查询移出 UI 线程）、ADR-050（帧内存缓存切设备即时填充）

## Context

ADR-045/047/050 之后仍残留：500 点位设备每 200ms 一帧，OnFrame 对每点逐个 item.Update（设 4 个 ObservableProperty：值/质量/时间/IsBad），跨 _ui.Post 在 UI 线程执行 → 约 500×4×5fps ≈ 1 万属性通知/秒，DataGrid 单元格刷新把 UI 线程压满；ComboBox 下拉/滚轮/点按与帧刷新同线程排队被饿死；窗口失焦未暂停（MainWindow 只处理 StateChanged 最小化，未处理 Deactivated）；DataGrid 未显式确认虚拟化。50 点位设备不卡 → 开销与点位数线性相关。

## Decision

- D1 表格节流（治本）：值进内存与刷表格解耦——每帧值仍 O(1) 入 _latestByPoint（无通知），DataGrid 行按节流周期（默认 500ms）经 RefreshGridFromCache 批量刷一次（1 万→4 千通知/秒，2.5~5 倍）；表格值最多滞后 ≤ 节流周期（监控无感）。
- D2 失焦暂停（补漏）：MainWindow 加 Deactivated → SetRealtimeVisible(false)、Activated → SetRealtimeVisible(true)（恢复用内存缓存一次补齐表格）；用 WindowState == Minimized 守卫避免还原时 Activated 先于 StateChanged 在仍最小化时恢复刷新。
- D3 DataGrid 显式开虚拟化：EnableRowVirtualization/EnableColumnVirtualization/CanContentScroll/VirtualizingPanel.IsVirtualizing/VirtualizationMode=Recycling（模板为标准 DataGridCellsPresenter，不破坏虚拟化），500 行只渲染可见行。
- D4 备选（先不做）：切设备/点位「先停后起」——步骤 1 落地实测后再定。

## Alternatives

- 降低采集帧率：丢数据，不可取。
- 去掉逐点属性通知但整体重建网格：通知量仍大、布局重。
- 「先停后起」切设备：改动大，先做节流实测。

## Rationale

节流降 2.5~5 倍通知量、监控无感；失焦暂停直接解决「切窗口卡、切回还卡一阵」；虚拟化只渲染可见行；曲线仍 500ms 固定刷新不变。

## Consequences

- 表格显示值最多滞后 ≤ 500ms（监控无感）；失焦后实时页后台停止刷新、切回恢复并用内存缓存补齐表格。
- 采集/存储/转发链路不动。
