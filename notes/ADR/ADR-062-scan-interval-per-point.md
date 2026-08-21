# ADR-062: 接通点位级 ScanIntervalMs 降频采样（ADR-053「第二刀」）

- 日期: 2026-08-21 | 状态: 已实现（2026-08-21，单测 705 通过 / 集成 51 通过；未提交，git 提交默认由用户执行）| 关联: ADR-052 问题 4、ADR-053 本轮不做项、worklog 2026-08-21 数据库实测（杠杆 2）
- 一句话结论: **`DevicePoint.ScanIntervalMs` 是"能配置的死字段"——DB/API/UI 全程可配可存可同步，唯独采集引擎从不读它**，每轮仍按全局 `Collection:IntervalMs`(1000ms) 全量读 enabled 点位。接线的唯一断点在消费端，本次在 `DeviceReader` 内按点位到期时间筛选，每轮只把到期点位传给驱动批量读取。

## 一、断点（问题 + 代码位置）

- `DeviceReader.ReadDeviceAsync`（`src/NitroGateway.Collection/DeviceReader/DeviceReader.cs` ~L54）：`var points = device.Points.Where(p => p.Enabled).ToList();` —— 只过滤 Enabled，**不读 `ScanIntervalMs`**。
- `DeviceCollector.CollectDeviceAsync`（`Collector/DeviceCollector.cs`）：每轮无差别调用 `ReadDeviceAsync`，读全量 → 全量落库/转发。
- 配置链路全通（无需改）：`PointEntity.ScanIntervalMs`(M003) + `DomainMapper` 双向、`DevicesController` L122/136/218、Web `PointList.vue`/`types.ts`、Desktop `PointEditor`（校验 ≥0、"0=继承设备默认"）、`PointManager.cs` L115 负数拦截、`ConfigSyncService`/`CenterConfigClient`。
- `DeviceReader/DESIGN.md` L79 已把此功能标为 v2 待办（"按点位 ScanIntervalMs 分组，独立采样"）。

## 二、修复设计

**语义**：`ScanIntervalMs=0`（默认）→ 继承全局 `Collection:IntervalMs`（1000ms）；`>0` → 点位独立采集间隔（如 5000 = 5s 一次）；`<0` 已被校验拦截。首次启动或新点位 → 立即读（无历史缓存）。`Collection:DeadbandHeartbeatMs`（5min 兜底）留在 ChangeDetector，不受 ScanInterval 影响。

**状态位置（关键）**：`DeviceReader` 已注册为 **Singleton**（`CollectionServiceCollectionExtensions`），`DeviceCollector` 是 **Scoped**（每轮新建 scope）——「上次采集时间」必须放 Reader（或独立 Singleton），放 Collector 会每轮重置导致永不生效。用 `ConcurrentDictionary<Guid, DateTime> _lastScannedAt`（pointId → 上次采集 UTC），进程重启自然清空 → 首次全读，与 ChangeDetector 行为一致。

**三态判定（DeviceReader 内部）**：
1. 无 enabled 点 → 返回空列表，仍走 ADR-031 真实探活（现状不变，不回归）；
2. 有 enabled 点但全部未到期 → 返回「跳过」标记（新增结果语义，`ReadDeviceAsync` 签名不变，接口只增不删）；
3. 有到期点 → 仅把到期点传给 `ReadBatchAsync`（Modbus/S7 批量读取天然支持子集）并更新 `_lastScannedAt`。

**跳过时的行为（DeviceCollector）**：不调驱动、不触发熔断（TryEnterProbe/RecordSuccess/RecordFailure 全不碰）、不更新健康快照（保持上次状态，既不误报在线也不误判离线）。设备健康探活节奏随之拉长为「最快到期点位间隔」——这是用户主动降频的预期结果。

## 三、行为变更（G1）

- 设了 `ScanIntervalMs>0` 的点位：从「每秒全量」变「按间隔采样」，对应 DB 落库 / MQTT 转发 / SignalR 推送频率同降（数据量杠杆 2）；
- 未设（=0）的点位：行为与现状完全一致（向后兼容）；
- 消费者语义（存储/转发/告警/SignalR/死区心跳）不改，只受采集频率影响。

## 四、测试计划（红绿对照，`DeviceReaderTests` / `DeviceCollectorTests`）

- `ScanIntervalMs=0` → 每轮都读；
- `ScanIntervalMs=3000` + 全局 1000 → 第 0/3/6… 轮读，中间轮跳过；
- 首次/新点位 → 立即读；
- 全部未到期 → 跳过（不调驱动/不上报/不熔断）；
- 无 enabled 点 → 仍探活（ADR-031 回归）；
- 多设备并发下 `ConcurrentDictionary` 线程安全。
- 收尾 `dotnet build NitroGateway.slnx` + 全量测试。

## 五、本轮不做

- 不改 DB schema（`ScanIntervalMs` 已存在）；不加设备级默认间隔字段（`DevicePoint.ScanIntervalMs` 注释的「继承设备默认」实为继承全局 `Collection:IntervalMs`）；
- 不做每点独立调度线程——仍由全局 1s 轮询驱动，仅筛选到期点（点位 >500 或需更精细抖动再评估 v2）。
