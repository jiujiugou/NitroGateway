# ADR-025: B 方案设计——桌面采集端 + MQTT + 云端中心（Ingest）

- 日期: 2026-08-10 | 状态: P0/P1 已实施（2026-08-10）；P2 待办（演进，非闭环缺口） | 来源: 方向讨论——A 方案（单机内嵌 Web）与 B 方案共用桌面壳，B 为生产演进形态（多现场 → 一中心）
- 范围: 新增 `src/NitroGateway.Ingest`；`docker-compose.yml` 新增 ingest 服务；复用 Forwarder 契约与中心 SQLite（现有迁移）

## 设计目标（七问摘要）
- 目标: 多现场采集端（桌面/网关）→ MQTT → 中心订阅入库 → Webapi/Vue 展示；桌面关不影响中心，断网不丢数据
- 边界: v1 只做遥测上行；不做设备/点位元数据同步与配置下发（P2 演进）
- 数据流: `Collection`(1s) → `DataDispatcher` → `Forwarder`(5s 批量 QoS1) → broker → `Ingest`(订阅 `nitrogateway/+/measurements`) → 中心 SQLite → `MeasurementsController` → Vue

## 契约（复用现有，不新增格式）
- topic: `nitrogateway/{deviceId}/measurements`（`src/NitroGateway.Forwarder/Forwarder.cs:105` 现成）
- payload: `BatchMeasurements` camelCase JSON（`JsonMessageSerializer` 现成）；P1 在顶层加 `v=1` 版本字段，兼容读取
- QoS: 1（至少一次）
- 幂等键: `BatchMeasurements.Id` / `MeasurementRecord.Id`（Guid）→ 中心 `measurements.id` 主键冲突即重复

## 关键决策
- D1 Ingest 独立项目 + 独立容器（compose 新增服务，复用 Dockerfile 入口切 Ingest Host）：故障隔离、独立扩缩容；代价多一个部署单元
- D2 幂等用记录级主键冲突（INSERT OR IGNORE），不做批次级去重表：简单可靠；极端重复下部分记录已存在属预期（至少一次语义，数据最终一致）
- D3 中心库与现场库同 schema（复用 M001~ 迁移），先 SQLite 单库：部署最简单；容量超限时 Ingest 是唯一写点，换时序库成本低
- D4 迟到消息按设备 Timestamp 正常入库（不修正不丢弃）：时序查询按 timestamp，展示不受影响；P2 再考虑迟到修正
- D5 中心写失败: 重试 3 次指数退避 → 丢弃 + 指标/日志（桌面端已保证至少一次；中心故障暴露给运维而非阻塞链路）

## 失败模式（复用已有能力）
- 断网 → 桌面 `forward_buffer` 排队重传（M002 + DeadLetter 已有）
- 重复 → 主键冲突幂等（D2）
- broker 重启 → `MqttClientWrapper` 自动重连重订阅（ADR-006 P1-2 已有）
- 消息损坏 → 反序列化失败丢弃 + 日志 + 指标，不阻塞后续

## 后续
- P2 元数据同步/配置下发、WPF 桌面壳（另立 ADR-026/ADR-033）——本 ADR 只定遥测上行与中心入库形态，配置下发与桌面壳属后续演进。
