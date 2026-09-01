# ADR-050: 桌面实时页切换设备卡——全历史最新值扫描

- 日期: 2026-08-16 | 状态: 已实施
- 来源: 用户反馈「实时设备绘图在切换和整体上讲，感觉还是有些卡」
- 关联: ADR-045（图表内存/主线程）、ADR-047（SQLite 查询移出 UI 线程）

## Context

ADR-045/047 修复后切换设备/点位仍可感知卡顿。根因：切换设备时 LoadPointsAsync 仍执行 _store.QueryLatestAsync(deviceId, pointId: null)——SQL 为 ROW_NUMBER() OVER (PARTITION BY point_id ORDER BY timestamp DESC) 对该设备全部历史（30 天保留 × 点位数）做窗口扫描取每点最新值。虽已包 Task.Run 移出 UI 线程（ADR-047），但网格要等查询返回才填充，随表增长查询耗时线性变慢。次要开销：网格重建逐条 Add（N 次 CollectionChanged 通知）。

## Decision

- D1 帧内存最新值缓存：RealtimeViewModel 新增 Dictionary<Guid, PointSnapshot> _latestByPoint，OnFrame 对每帧所有测量点以 O(1) 写入（无 UI 通知、不随切设备清空）。
- D2 切设备即时填充：LoadPointsAsync 先用「配置 + _latestByPoint」立即重建网格（Points 改 RingObservableCollection + 单次 Replace），不再等 DB；取消选中才立即清空，切设备时把清空延后到 Replace 一次重建（避免 Clear + Replace 两次整表通知）。
- D3 DB 兜底降级：仅当所选设备存在从未在帧中出现过的点位（冷启动/离线）时，后台跑一次 QueryLatestAsync 填充缺失；结果只填仍缺失的点位（帧数据更新鲜，以帧为准、不覆盖）。

## Alternatives

- 保留全历史查询但优化 SQL：仍随表增长线性变慢。
- 预加载全部点位最新值到内存：点位多时内存与启动开销大。

## Rationale

帧内存已持有最新值，DB 全历史窗口扫描是冗余且随表增长退化；以帧为准避免覆盖更新鲜数据；RingObservableCollection 单次 Replace 减少整表通知。

## Consequences

- 在线设备切换不再等待全历史最新值查询、网格即时有值；冷启动/离线由后台 DB 兜底填充。
- 采集/存储/转发链路不动。
