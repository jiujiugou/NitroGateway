# ADR-046: 桌面设备状态通知丢失与线程越界（DevicesViewModel OnFrame，2026-08-15）

- 日期: 2026-08-15 | 状态: 已定位，待实施 | 来源: 用户反馈「桌面端设备管理状态的通知有问题」
- 关联: ADR-026（桌面 UI 帧桥）、ADR-027（桌面代码评审）、ADR-037（S3 防重入 / S7 diff 刷新）

## 问题
- 现象：设备管理页在线/离线状态的通知（健康变更帧 → 即时刷新）不稳定——
  - 状态不即时更新：健康变更帧到达时若恰逢刷新在途，通知被 `IsLoading` 防重入闸直接吞掉（最长延迟到 5s 定时器）；
  - 通知处理线程越界：`OnFrame` 在 EventBridge 后台线程触发，却直接读写 ViewModel 属性（跨线程 PropertyChanged / StatusText）。
- 代码位置（均在 `src/NitroGateway.Desktop/ViewModels/DevicesViewModel.cs`）：
  - L76 `_bridge.FrameReady += OnFrame;` 订阅健康变更通知帧；
  - L249-253 `OnFrame`：`HealthChanges.Count==0` 跳过，否则 `_ = RefreshAsync();`（fire-and-forget）；
  - L184-246 `RefreshAsync`：L186 `if (IsLoading) return;` 丢通知；L189/L245 `IsLoading` 跨线程置位；L241 失败路径 `StatusText` 后台线程写入。
- 根因：
  1. 通知入口与 5s 定时器共用 `RefreshAsync` 的防重入闸（L186），在途刷新会把本应即时消费的健康变更帧丢弃；
  2. 帧通知在后台线程产生，ViewModel 未把 `IsLoading/StatusText` 的写操作贴回 UI 线程（仅列表更新走了 `_ui.Post`）；
  3. 每次健康变更都做全量 `_cache.GetAllAsync()` + 全列表 diff，无按设备去重/节流，通知突发时重叠刷新。

## 修复方向
- 通知不再复用防重入闸：`OnFrame` 记录待刷标志，当前 `RefreshAsync` 完成后若有待刷标志则补刷一次（保底 5s 定时器不变）；
- 线程归位：`OnFrame` 内只做 `_ui.Post` 队列，`IsLoading`/`StatusText`/`DeviceCountChanged` 一律在 UI 线程置位；
- 可选轻量化：健康变更帧按 `DeviceId` 直接原位更新对应 `DeviceItem.Status`（复用 `_health.GetSnapshot`），避免全量 DB 查询；
- 验证：DevicesViewModelTests 增「刷新在途到达健康变更帧 → 结束后补刷」与「OnFrame 不跨线程写属性」用例；build + 全量单测红绿对照。
