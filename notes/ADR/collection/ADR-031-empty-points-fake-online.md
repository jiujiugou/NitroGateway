# ADR-031: 空点位设备断链后假在线——健康监控收不到失败

- 日期: 2026-08-11 | 状态: 已修复（2026-08-11） | 来源: 用户反馈（断开所有连接后只有部分设备离线，其余仍在线；连接测试显示断开但设备管理仍在线）
- 范围: src/NitroGateway.Protocol/Modbus/ModbusDriverBase.cs、src/NitroGateway.Protocol/S7/S7Driver.cs

## 问题
- 断开所有 PLC 后，有点位的设备正常判离线并通知前端；空点位（0 启用点位）设备全部仍显示在线
- 连接测试（新建驱动 ConnectAsync+PingAsync，真实通信）显示断开，但采集侧健康监控收不到失败 → DB/前端状态滞留 Online，前端不推送离线通知
- 根因: 驱动 State=Connected 后不再发任何数据；ReadBatchAsync 对空点位列表直接返回空成功（ModbusDriverBase.cs、S7Driver.cs），TCP 断开无感知 → HealthMonitor 持续 ReportSuccess
- 现场佐证: nitrogateway.db 10 台设备，2 台有点位=Offline，8 台 0/0 空点位=Online

## 修复（2026-08-11）
- Modbus/S7 的 ReadBatchAsync 空点位分支改为发一次真实探测读验证链路：Modbus 读寄存器 0（与 PingAsync 一致），S7 读 PingAddress（默认 DB1.DBW0）；成功返回空列表，失败置 Faulted 并返回 Failure → 连续 3 次失败判离线 → 熔断 Trip+Evict
- 测试: ModbusDriverBaseTests / S7DriverTests 红绿对照（探测失败→Failure+Faulted；探测成功→空成功）

## 追加修复（2026-08-11）：HealthReporter 语义修正（用户质疑）
- 问题: Report(successCount, failCount) 中 successCount 从未被读取（死参数）；failCount>0 把点级质量差（如单个点位缩放失败 Uncertain）当成设备失败 → 连打 3 轮整台设备判离线 + 熔断停采，与 DeviceCollector 自身注释 读取成功（含点位质量差）只上报成功 矛盾；catch{} 静默吞异常，状态滞留时无日志线索
- 修复: IHealthReporter.Report 改为 (deviceId, deviceName, succeeded, errorMessage)——设备健康只看链路成败；DeviceCollector 成功路径恒传 true（firstBad 明细仅作诊断信息）；HealthReporter catch 记 Warning 日志（含设备名/ID）
- 测试: HealthReporterTests 4 例（成功/失败/默认消息/异常吞掉不传播）+ DeviceCollectorProbeTests 点级质量差仍上报成功（红绿对照）；Unit 428 全绿 + Integration 43 全绿 + build 0 错误
