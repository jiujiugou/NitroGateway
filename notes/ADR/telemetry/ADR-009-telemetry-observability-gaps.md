# ADR-009: Telemetry 可观测性缺口清单

- 日期: 2026-08-07 | 状态: 全部条目已处理（2026-08-09）——P1-1/P1-2/P2-1~P2-4 已修复
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除本清单对应条目

## 处理记录（2026-08-09）

- P1-1 `nitro_collection_duration_ms` 补上报点：`DeviceCollector.CollectOnceAsync` 用 Stopwatch 计时整轮并行采集，`finally` 中 `CollectionDurationMs.Observe(...)`（不含设备列表获取）。测试：`CollectOnceAsync_ReportsOnlineAndDurationMetrics`。
- P1-2 `nitro_devices_online` 补上报点：`CollectOnceAsync` 在 `DevicesAvailable.Set` 同处刷新 `DevicesOnline.Set(HealthMonitor 快照 Online 数)`，与 available（待采集数）语义区分。测试：同上。
- P2-1 `forward_total` deadletter 标签：`SqliteForwardBuffer.MarkFailedAsync` 超限进死信处补 `WithLabels("deadletter").Inc()`（Forwarder 无法感知死信转换，故在转换发生点上报）。测试：`MarkFailed_OverMaxRetries_ReportsDeadletterMetric`（存在性断言，因损坏负载恢复路径也会累加）。
- P2-2 `mqtt_state` help 文本：改为与 `MqttConnectionState` 枚举序一致（0=Disconnected 1=Connecting 2=Connected 3=Reconnecting 4=Faulted）。
- P2-3 文档漂移：`docs/03-功能清单.md` F-23 与 `docs/01-盘点.md` 指标数 8 → 9；`docs/interview/telemetry/` questions/answers 同步为"已修复"状态。
- P2-4 重复引用：Telemetry csproj 删除重复的 OpenTelemetry PackageReference；决策：OpenTelemetry 保留为预留入口不接执行层（生产无 Listener/导出器，追踪 dormant），已在 `TelemetryServiceCollectionExtensions` 注释说明。

## 验证
- `dotnet build` 0 错误；UnitTests 199 通过（上轮 197 + 新增 2）；IntegrationTests 25 通过