# ADR-048: 桌面实时页设备下拉不随新增设备刷新（2026-08-15）

- 日期: 2026-08-15 | 状态: 已实施 | 来源: 用户反馈「新增设备后实时数据窗口设备数据未增加」
- 关联: ADR-027（桌面代码评审）、ADR-045（实时页生命周期）、ADR-047（实时页查询异步化）

## 问题
- 现象: 设备页新增设备后，切到「实时数据」页，设备下拉列表里没有新设备。
- 根因: `RealtimeViewModel` 只在构造时 `_ = LoadDevicesAsync()` 加载一次设备下拉 `Devices`；
  新增/编辑/删除设备走 `DevicesViewModel` → `DeviceManager.RegisterAsync/UnregisterAsync`
  → `DeviceSnapshotCache.Invalidate()`（缓存已失效），但没有任何机制通知实时页重载下拉。
  `DevicesViewModel.RefreshAsync` 只刷自身列表并触发 `DeviceCountChanged`（5s 定时也触发），不改实时页下拉。
- 代码位置: `src/NitroGateway.Desktop/ViewModels/RealtimeViewModel.cs`
  （`LoadDevicesAsync`/`OnIsActiveChanged`）；`ViewModels/DevicesViewModel.cs`（Add/Edit/Delete → RefreshAsync）。

## 修复方向
- 重新进入实时页（`IsActive` 重新置 true）时重载设备下拉：`OnIsActiveChanged(true)` 分支加
  `_ = LoadDevicesAsync()`；`LoadDevicesAsync` 由「清空重建」改为「增量对账」——
  删除已不存在的设备、新增追加到末尾、重命名替换对应项（DeviceOption 是记录需换新实例），
  不清空重建避免 ComboBox 选中丢失；选中设备仍存在则按 Id 重指向最新项，被删除则清空选中。
- 不监听 `DeviceCountChanged`（5s 定时触发，会无谓刷新并可能打断选择）。

## G1（行为变更）
- 实时页激活时多一次设备目录读取（内存热路径 + EF Core 真异步，代价可忽略），下拉随配置变化刷新。

## 实施（2026-08-15）
- `src/NitroGateway.Desktop/ViewModels/RealtimeViewModel.cs`:
  - `OnIsActiveChanged(bool value)` `value==true` 分支新增 `_ = LoadDevicesAsync();`。
  - `LoadDevicesAsync()` 改为取到结果后 `_ui.Post(() => ApplyDeviceDiff(latest))`；
    新增私有 `ApplyDeviceDiff(IReadOnlyList<Device>)` 做增量对账（见修复方向）。
- 测试（`tests/NitroGateway.UnitTests/RealtimeViewModelTests.cs`）:
  - 新增 `Re_activate_reloads_devices_and_shows_newly_added_device`（重激活后新设备出现在下拉）；
  - 新增 `Re_activate_keeps_selected_device_when_renamed`（重命名后下拉更新且选中按 Id 保持）；
  - 既有 `IsActive_false_skips_frames_and_detaches_chart_on_deactivate` 重激活处补一次目录结果入队。
- 验证: `dotnet build NitroGateway.slnx` 0 错误（警告均为既有 NU1903/NU1701）；`dotnet test
  tests/NitroGateway.UnitTests --no-build` 604/604 通过（基线 602 + 新增 2）。未提交（git 由用户执行）。
