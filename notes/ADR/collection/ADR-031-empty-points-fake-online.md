# ADR-031: 空点位设备断链后假在线——健康监控决策

- 日期: 2026-08-11 | 状态: 已实施
- 来源: 用户反馈（断开所有连接后只有部分设备离线，其余仍在线；连接测试显示断开但设备管理仍在线）

## Context

驱动 State=Connected 后不再发任何数据；ReadBatchAsync 对空点位列表直接返回空成功（ModbusDriverBase/S7Driver），TCP 断开无感知 → HealthMonitor 持续 ReportSuccess，空点位设备断链后仍显示在线。现场佐证：nitrogateway.db 10 台设备，2 台有点位=Offline，8 台 0/0 空点位=Online。

## Decision

- D1 空点位分支真实探活：Modbus/S7 的 ReadBatchAsync 空点位列表改为发一次真实探测读验证链路（Modbus 读寄存器 0，与 PingAsync 一致；S7 读 PingAddress 默认 DB1.DBW0）；成功返回空列表，失败置 Faulted 并返回 Failure → 连续 3 次失败判离线 → 熔断 Trip+Evict。
- D2 HealthReporter 语义修正：IHealthReporter.Report 改为 (deviceId, deviceName, succeeded, errorMessage)——设备健康只看链路成败；DeviceCollector 成功路径恒传 true（firstBad 明细仅作诊断信息）；HealthReporter catch 记 Warning 日志（含设备名/ID），不再静默吞异常。

## Alternatives

- 空点位直接判离线：误伤（设备可能正常在线）。
- 保留空点位恒成功：链路断开无感知，假在线持续。

## Rationale

空点位设备仍应真实探活，链路断开必须可感知；设备健康判定应以链路成败为准，点级质量差不该让整台设备误判离线；catch 吞异常时状态滞留无日志线索，必须记录。

## Consequences

- 空点位设备断开后正确判离线并通知前端；点级质量差不误判整设备离线。
- 异常路径有 Warning 日志可诊断；DeviceCollector 与 HealthReporter 语义一致。
