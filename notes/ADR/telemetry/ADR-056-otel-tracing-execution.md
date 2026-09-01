# ADR-056: OpenTelemetry 追踪执行层启用

- 日期: 2026-08-19 | 状态: 已实施
- 背景: ADR-009 P2-4 曾决策「OpenTelemetry 保留为预留入口不接执行层」

## Context

各模块 StartActivity 埋点与 tag 常量齐全，但生产无 ActivityListener/导出器 → StartActivity 返回 null，追踪 dormant。用户要求「把 Telemetry 用起来」。

## Decision

- D1 接入 OpenTelemetry 追踪执行层，不动现有指标：新增 3 包（锁 1.17.0，与既有 OpenTelemetry 核心解析版本对齐，未升级现有包）：OpenTelemetry.Extensions.Hosting、Exporter.Console、Exporter.OpenTelemetryProtocol。
- D2 新增 TelemetryTracingOptions：Telemetry:Tracing 段（Enabled=true、Exporter=Otlp/Console/None、Endpoint、Protocol=Grpc/HttpProtobuf、ServiceName）；非法值静默回退默认，不阻断启动。
- D3 AddNitroTelemetry(IConfiguration, serviceName)：先注册指标，再按配置 AddOpenTelemetry().WithTracing(...)（AddSource("NitroGateway")；Otlp 走 Endpoint/Protocol，Console 走 AddConsoleExporter）；Enabled=false/Exporter=None 不注册 TracerProvider，保持 dormant（兼容 ADR-009）。
- D4 入口：Webapi（service.name=nitrogateway-webapi）、Ingest（nitrogateway-ingest）；两处 appsettings 加 Telemetry:Tracing 段。

## Alternatives

- 保持 dormant：追踪继续没被用起来，与用户要求相悖。
- 只接 Console 导出器：无生产观察价值。

## Rationale

执行层埋点已齐全，只差 ActivityListener/导出器；锁 1.17.0 与既有核心版本对齐避免升级风险；Enabled=false/None 保持 dormant 向后兼容。

## Consequences

- 生产接入需把 Telemetry:Tracing:Endpoint 指到 jaeger/tempo/otel-collector（当前默认 localhost:4317）；无 collector 可 Enabled=false 或 Exporter=None 关闭。
