# ADR-053: 数据量控制——三处一起瘦身：死区变化抑制 + 心跳兜底（ADR-052 问题 4 修复）

- 日期: 2026-08-16 | 状态: 已拍板（2026-08-16 用户确认「DB 落库 + MQTT 转发 + SignalR 推送三处一起瘦身」） | 关联: ADR-052 问题 4、ADR-050/051（实时页查询）
- 一句话结论: **盘上峰值 = 每秒实写行数 × 行大小 × 保留天数**。本次主刀「死区变化抑制」（值没变就不存不传不推）+ 心跳兜底（300s），存储(SQLite)、转发(MQTT)、推送(SignalR) **三处共用同一放行子集**；桌面实时图/告警仍收全量（内存态，不受影响）。按 600 点估算，本地盘从 ~310GB/30 天 → ~0.6-1.2GB/30 天，SignalR 每秒推送从 ~600 条 → 仅变化点。

## 一、背景与根因

- 采集: `Collection:IntervalMs=1000`（1s 全量扫），`DeviceCollector` 每轮读全部 enabled 点 → 全部落库，**无变化检测**。
- 死区现状: `PointValuePipeline.ConvertSingle` 里死区只更新 `_lastValues`（供告警 Duration 判定），注释明确「不丢弃数据——存储写入不受死区影响」；快照照常返回。
- 存储: `SqliteMeasurementStore.WriteAsync` 纯 `INSERT`，无去重。
- 转发: `DataDispatcher.ToBatchMeasurements` 把每个快照都入 MQTT 上行（云端同样被 1s 数据灌）。
- 推送: `DeviceStatusDispatcher`（SignalR）把每个快照都推给 Web 前端（600 点/s），参与前端卡顿。
- 结果: 600 点 × 86400 ≈ **5184 万行/天** ≈ 10GB/天、30 天 ~310GB——本地盘会撑爆，且 SignalR 全量推也加重 Web 页负担。

## 二、修复设计（已拍板方案）

### 抑制点与范围（关键）

- **抑制点不在 Pipeline，而在 `DataDispatcher.DispatchAsync` 内计算一次**——三个消费边界（DB / MQTT / SignalR）共用同一放行子集，保证语义一致。
- 新增纯类 `ChangeDetector`（`src\NitroGateway.Collection\Pipeline\ChangeDetector.cs`），维护每点 `lastStoredValue / lastStoredQuality / lastStoredAt`；方法 `Filter(snapshots, nowUtc)` 返回放行列表。
- `DataDispatcher` 接线：
  - `_measurement.Post(toStore)`（DB 只写变化点）；
  - `ToBatchMeasurements(deviceId, toStore)`（MQTT 只转变化点）；
  - `_sinks.Post(new PointStoredEvent { Snapshots = snapshots, PersistedSnapshots = toStore })`——**事件仍发全量**，EventBridge（桌面实时图）/ Alarm（告警 Duration）不受影响；`PersistedSnapshots` 携带实际落库/转发的子集（null 兼容旧调用方=全量）。
- `DeviceStatusDispatcher.OnStoredAsync`（SignalR）改用 `e.PersistedSnapshots ?? e.Snapshots`；空列表直接 return 不推送。

**放行语义（关键）**：
- `Deadband = 0`（默认）→ 保持现状，**每样本都写**（向后兼容，现有点位行为不变）；
- `Deadband > 0` → **变化抑制**：`|新工程值 − 最后已存值| < Deadband` 抑制不写；`≥ Deadband` 写（**恰好等于阈值视为变化**，与管线现有 `<` 抑制语义、以及「死区=最少变化量」的用户直觉一致）。

**死区值从哪来**：`PointSnapshot` 新增 `Deadband`（只增不改，默认 0），`PointValuePipeline` 组装快照时从 `DevicePoint.Deadband` 透传——`ChangeDetector` 纯读快照即可判定，不额外依赖点位定义查询。

**与告警解耦**：告警 Duration 继续用管道 `_lastValues`（每样本更新），不受抑制影响——两者用不同缓存，职责不混。管道仍全量下发（其类注释同步澄清：抑制在 Dispatcher 层）。

**必写边界（防止断档/漏告警）**：
1. **首样本必写**：新点位、或进程重启后第一条必写——天然免持久化 lastStored，重启后首条即新基线，无断档；
2. **质量变化必写**：Good↔Bad（含 Uncertain）切换必须写一条，前端/告警才能看到掉线/恢复；
3. **心跳兜底**：超过心跳间隔（`Collection:DeadbandHeartbeatMs`，默认 300s）即使值不变也强制写一条，保留"还活着"的证据、方便时间对齐；心跳同样经 SignalR 推送，前端凭「最后收到时间」判 stale 更准；
4. **Bool/String**：按值相等判定（无死区概念），值变了才写（心跳兜底同样生效）；
5. **缩放失败/NaN**：产出 Uncertain 快照，按"质量变化必写"落库；数值无法转 double 时保守放行（无法证明未变，宁写勿丢）。

### 本轮不做（后续可选）

- **点位级 ScanIntervalMs 降频**（第二刀）：字段已建未接线，本轮不动；用途是快变点用降频而非死区，慢变点用本次第一刀即可。
- **保留天数下调**（封顶）：现状 30 天保留不变，观测抑制后行速再评估。

## 三、影响估算（600 点、缓变为主、行 ~200B）

| 项 | 现状 | 本刀后（估算） |
|---|---|---|
| 行/天 | 5184 万 | ~10-20 万 |
| 盘/天 | ~10GB | ~20-40MB |
| 30 天 | ~310GB | 0.6-1.2GB |
| 每日 purge | 删 5000 万行 | 删 ~20 万行 |
| SignalR 推送 | ~600 条/s | 仅变化点+心跳 |

实时页"最新值"查询（ADR-050 的 ROW_NUMBER 全历史扫描）随表缩小而显著变快。

## 四、行为变更

- 设了死区的点位，历史从「每秒连续记录」变为「变化记录」（只存变化点 + 心跳）。
- **SignalR 推送同步抑制**（方案 B）：Web 实时页只收到变化点 + 心跳（300s），不再每秒全量 600 条；桌面实时图走 EventBridge 帧，**仍每秒全量**（内存态，不受影响）。
- 告警仍按每样本判定（收全量事件），趋势曲线按"最后存储值"补线。
- 前端两处适配：`HistoryView` 曲线改 `step:'end'`（稀疏变化点不再画假连续线）；`MonitoringView` 的 stale 改「最后收到时间超阈值」（默认 2×心跳=600s），静默点位由心跳续命、真正断流才标 stale。
- 需要"每秒连续历史"的点，保持 `Deadband=0` 即可——**死区本身就是每点的开关**。
- 已配置死区的既有点位，升级后立刻开始抑制（符合你设死区的预期），旧历史不清、不回改。

## 五、测试计划

- 新增 `ChangeDetector` 单测（纯类，红绿对照）：
  - 死区内不写 / 超死区写 / **恰好等于死区写**（边界，与管线 `<` 语义一致）；
  - 首样本必写（含重启=新实例）；
  - 质量变化必写（Good→Bad、Bad→Good）；
  - 心跳超时强制写（值未变也写）；
  - Bool/String 值变化判定；Uncertain/NaN 落库；
  - Deadband=0 时全部照写（向后兼容回归）。
- `DataDispatcherTests` 增补：抑制后 store/buffer 只收变化点、sink 事件仍收全量（`PersistedSnapshots` 只含变化点；全抑制时空列表）。
- 全量回归 `dotnet test`（基线 612 + 新增）。

## 六、配置

- `Collection:DeadbandHeartbeatMs`（毫秒，默认 300000）——心跳兜底间隔，`> 0` 校验；Webapi 与 Desktop 的 `appsettings.json` 同步。
- 死区本身是**点位级**配置（`DevicePoint.Deadband`，已有），无需全局配置；0=全量、>0=变化抑制。

## 七、本轮明确不做

- 不换存储引擎（InfluxDB/TimescaleDB——600 点规模 SQLite + 抑制足够）；
- 不清/不回改既有历史；
- 不新增告警/前端功能；
- 不碰 `Storage/` `Protocol/Abstraction/` 纯接口（`ChangeDetector` 放 Collection 内部即可）。
- 不做点位级降频（第二刀）与保留天数下调——观测本刀效果后再评估。
