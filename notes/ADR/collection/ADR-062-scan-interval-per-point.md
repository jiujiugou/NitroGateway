# ADR-062: 点位级 ScanIntervalMs 降频采样（ADR-053「第二刀」）

- 日期: 2026-08-21 | 状态: 已实施
- 关联: ADR-052 问题 4、ADR-053 本轮不做项

## Context

DevicePoint.ScanIntervalMs 是「能配置的死字段」——DB/API/UI 全程可配可存可同步，唯独采集引擎从不读它，每轮仍按全局 Collection:IntervalMs(1000ms) 全量读 enabled 点位。接线唯一断点在消费端：DeviceReader.ReadDeviceAsync 只过滤 Enabled、不读 ScanIntervalMs；DeviceCollector.CollectDeviceAsync 每轮无差别调 ReadDeviceAsync 读全量。DeviceReader/DESIGN.md L79 已把此功能标为 v2 待办。

## Decision

- D1 语义：ScanIntervalMs=0（默认）→ 继承全局 Collection:IntervalMs（1000ms）；>0 → 点位独立采集间隔（如 5000 = 5s 一次）；<0 已被校验拦截。首次启动或新点位 → 立即读（无历史缓存）。Collection:DeadbandHeartbeatMs（5min 兜底）留在 ChangeDetector，不受 ScanInterval 影响。
- D2 状态位置（关键）：DeviceReader 已注册为 Singleton，DeviceCollector 是 Scoped（每轮新建 scope）——「上次采集时间」必须放 Reader（或独立 Singleton），放 Collector 会每轮重置导致永不生效。用 ConcurrentDictionary<Guid, DateTime> _lastScannedAt（pointId → 上次采集 UTC）；进程重启自然清空 → 首次全读，与 ChangeDetector 行为一致。
- D3 三态判定（DeviceReader 内部）：
  1. 无 enabled 点 → 返回空列表，仍走 ADR-031 真实探活（现状不变，不回归）；
  2. 有 enabled 点但全部未到期 → 返回「跳过」标记（新增结果语义，ReadDeviceAsync 签名不变，接口只增不删）；
  3. 有到期点 → 仅把到期点传给 ReadBatchAsync（Modbus/S7 批量读取天然支持子集）并更新 _lastScannedAt。
- D4 跳过时的行为（DeviceCollector）：不调驱动、不触发熔断（TryEnterProbe/RecordSuccess/RecordFailure 全不碰）、不更新健康快照（保持上次状态，既不误报在线也不误判离线）。设备健康探活节奏随之拉长为「最快到期点位间隔」——用户主动降频的预期结果。

## Alternatives

- 每点独立调度线程：>500 点位或需更精细抖动再评估 v2，本轮不做。
- 改 DB schema 加设备级默认间隔字段：不加（ScanIntervalMs 已存在；注释的「继承设备默认」实为继承全局 Collection:IntervalMs）。
- 不加变化检测、不做每点独立线程：仍由全局 1s 轮询驱动，仅筛选到期点。

## Rationale

消费端接线即可实现降频，配置链路已全通；进程重启清空自然首次全读与 ChangeDetector 一致；跳过时不动熔断/健康快照避免误报。

## Consequences

- 设 ScanIntervalMs>0 的点位：从「每秒全量」变「按间隔采样」，对应 DB 落库 / MQTT 转发 / SignalR 推送频率同降（数据量杠杆 2）。
- 未设（=0）的点位：行为与现状完全一致（向后兼容）；消费者语义（存储/转发/告警/SignalR/死区心跳）不改。
