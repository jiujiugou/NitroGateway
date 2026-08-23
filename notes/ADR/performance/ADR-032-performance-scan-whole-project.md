# ADR-032: 全项目性能瓶颈扫描（2026-08-11）
- 日期: 2026-08-11 | 状态: 处理中（P1-2 已修复，其余待处理）
- 背景: 用户问"从整个项目讲，导致速度变慢的在什么地方"，对网关全链路（采集→存储→转发→Web API/SignalR→前端）做性能扫描；纯 review 无代码改动，未跑测试
- 范围: Collection/Persistence/Forwarder/Alarm/Protocol(S7)/Webapi(Hubs/Controllers)/web 前端

## P1 高影响（规模一大必先顶到）
- P1-1 `latest-batch` 全量扫设备历史：SqliteMeasurementStore.cs:245-251 用 ROW_NUMBER() PARTITION BY point_id 取每点最新，需扫描该设备全量 measurements；MonitoringView.vue onMounted 对每台设备并行调一次。1s 采样 × 30 天保留 ≈ 每点 259 万行，随保留期/点数线性变慢。方向：写路径维护 latest 快照表（写时 upsert，读 O(1)），或按时间窗裁剪只扫最近 N 分钟。
- P1-3 S7 逐点串行读：S7Driver.cs:211-215 ReadBatchAsync 逐点 await ReadAsync（每点一次 TCP 往返 + 闸门），单设备采集耗时 = 点数 × RTT。方向：按 DB 区连续块读（HSL 一次读多地址），能力声明同步修正（ADR-019 P3-4 已记录 SupportsBatchRead=false）。
- P1-4 SignalR Outbox 单线程串行：OutboxConsumer.cs:31-43 逐条 await SendAsync；单个慢 WebSocket 客户端拖慢全部设备推送；Channel 容量 1000 DropOldest。方向：受控并发发送 + 慢连接剔除，或前端合并帧（桌面端 EventBridge 已有先例）。

## P2 规模相关（中大规模才明显）
- P2-1 转发逐批串行 QoS1：Forwarder.cs:96-129 每批一次 PublishAsync 串行 await，Broker RTT 高时每轮排水量远小于 2000 批上限，积压→入队 DropOldest 丢数风险。方向：受控并发发布（8-16），或按设备合并 topic 负载。
- P2-2 写入热路径开销 + 单写者吞吐：SqlitePragmas.cs 每连接 2 次 PRAGMA 往返；MeasurementWriteHost 单消费者串行写，Channel 1000 批 DropOldest，写不及时静默丢批（有指标但无自愈）。死区（PointValuePipeline.cs）当前不减少写入量（注释明确存储/SignalR 不受死区影响），1s 全量写与点数成正比。方向：攒批合并写、容量/死区语义配置化、监控 StoreWriteFailures。
- P2-3 全 TEXT 主键/时间戳放大库与扫描成本：measurements/forward_buffer 的 id/device_id/point_id/timestamp 均用 Guid "D"/"O" 字符串（M001/M002），索引体积与比较成本约为 INTEGER/BLOB 的 2-3 倍。方向：新库改 BLOB(16)+INTEGER epoch（迁移属行为变更，需 G1）。

## P3 轻微（暂可接受）
- P3-1 AlarmHostedService.cs:102 每快照 rules.Where().ToList() 分配；DeviceSnapshotCache TTL 10s + 配置写失效触发全量 EF Include 重载（大配置时注意）。方向：小改或观察。

## 处理记录（2026-08-11）
### P1-2 告警规则每设备每轮查库（已修复）
- 修复: 新增 `AlarmRuleCache`（Singleton，TTL 兜底 30s + 写失效）与 `CachedAlarmRuleRepository`（Scoped 装饰器，读走缓存、写透传并在成功后失效）；`AddNitroSqlite` 注册改为 内层具体类型 + 缓存 + 装饰器（SqliteServiceCollectionExtensions.cs）
- 语义: 与内层一致（设备+点位+Enabled 过滤）；加载失败不落缓存、下条事件重试，故障行为与直查相同
- 测试: CachedAlarmRuleRepositoryTests 8 例（缓存命中/失效/失败重试/TTL/跨装饰器共享）；Unit 436 全绿 + Integration 43 全绿 + build 0 错误
