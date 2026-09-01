# ADR-009: Telemetry 可观测性缺口决策

- 日期: 2026-08-07 | 状态: 已实施（2026-08-09）；其中 OpenTelemetry 预留决策后由 ADR-056 启用

## Context

Telemetry 指标存在采集/转发关键指标缺失、标签语义不清、文档漂移；OpenTelemetry 依赖已引入但未接执行层。

## Decision

- D1 nitro_collection_duration_ms 在 DeviceCollector.CollectOnceAsync 用 Stopwatch 计时整轮并行采集（不含设备列表获取），finally 中 Observe。
- D2 nitro_devices_online 在 DevicesAvailable.Set 同处刷新为 HealthMonitor 快照 Online 数，与 available（待采集数）语义区分。
- D3 forward_total deadletter 标签：在 SqliteForwardBuffer.MarkFailedAsync 超限进死信处 WithLabels("deadletter") 上报（Forwarder 无法感知死信转换，故在转换发生点上报）。
- D4 OpenTelemetry 保留为预留入口、不接执行层：生产无 Listener/导出器，追踪 dormant（TelemetryServiceCollectionExtensions 注释说明）。——后由 ADR-056 决策启用执行层。
- D5 文档同步：功能清单指标数、interview 问答更新为已修复；清理 Telemetry csproj 重复的 OpenTelemetry PackageReference。

## Rationale

- 补关键采集/在线/死信指标，运维可观察采集耗时、在线数与死信增长；OTel 预留避免半接入的无效开销；指标命名以实际暴露为准并同步文档。

## Consequences

- /metrics 暴露更完整的采集与死信指标；OpenTelemetry 追踪在 ADR-056 前保持 dormant；文档与实现一致。
