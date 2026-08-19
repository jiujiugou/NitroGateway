# ADR-056: OpenTelemetry 追踪执行层启用

- 日期: 2026-08-19 | 状态: 已实施
- 背景: ADR-009 P2-4 曾决策"OpenTelemetry 保留为预留入口不接执行层"。各模块 `StartActivity` 埋点与 tag 常量齐全，但生产无 ActivityListener/导出器 → `StartActivity` 返回 null，追踪 dormant（用户：把 Telemetry 用起来）。
- 方案: 接入 OpenTelemetry 追踪执行层，不动现有指标。
  - 新增 3 包（锁 1.17.0，与既有 OpenTelemetry 核心解析版本对齐，未升级现有包）：`OpenTelemetry.Extensions.Hosting`、`Exporter.Console`、`Exporter.OpenTelemetryProtocol`。
  - 新增 `Tracing/TelemetryTracingOptions.cs`：`Telemetry:Tracing` 段（Enabled=true、Exporter=Otlp/Console/None、Endpoint、Protocol=Grpc/HttpProtobuf、ServiceName）；非法值静默回退默认，不阻断启动。
  - `TelemetryServiceCollectionExtensions` 新增 `AddNitroTelemetry(IConfiguration, string? serviceName)`：先注册指标，再按配置 `AddOpenTelemetry().WithTracing(...)`（`AddSource("NitroGateway")`；Otlp 走 Endpoint/Protocol，Console 走 AddConsoleExporter）；`Enabled=false`/`Exporter=None` 不注册 TracerProvider，保持 dormant（兼容 ADR-009）。
  - 入口：Webapi（service.name=nitrogateway-webapi）、Ingest（nitrogateway-ingest）；两处 appsettings 加 `Telemetry:Tracing` 段。
- 改动文件:
  - `src/NitroGateway.Telemetry/`：csproj + TelemetryServiceCollectionExtensions.cs + Tracing/TelemetryTracingOptions.cs
  - `src/NitroGateway.Webapi/`：Program.cs + appsettings.json
  - `src/NitroGateway.Ingest/`：Program.cs + appsettings.json
  - `tests/NitroGateway.UnitTests/Telemetry/`：TelemetryTracingOptionsTests（7）+ TelemetryServiceCollectionExtensionsTests（4）
- 验证:
  - `dotnet build NitroGateway.slnx` 0 错误；`dotnet test tests/NitroGateway.UnitTests --no-build` **633 通过 / 0 失败**（基线 622 + 新增 11）。
  - 冒烟：`Telemetry__Tracing__Exporter=Console` 启动 Webapi，日志出现真实 span（Forward，scope Name: NitroGateway，service.name=nitrogateway-webapi，TraceFlags=Recorded / Status=Ok）。
  - 冒烟产生的 `smoke.db*` 运行时文件已删除。
- 后续: 生产接入需把 `Telemetry:Tracing:Endpoint` 指到 jaeger/tempo/otel-collector（当前默认 localhost:4317）；无 collector 可 `Enabled=false` 或 `Exporter=None` 关闭。
