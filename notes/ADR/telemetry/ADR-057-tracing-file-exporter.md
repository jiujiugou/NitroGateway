# ADR-057: 追踪 File 导出器（JSONL 落盘本地观察）

- 日期: 2026-08-19 | 状态: 已实施
- 背景: ADR-056 接入 OTLP/Console 后，用户问"追踪在哪里观察？日志里没看出来"。根因：默认 `Exporter=Otlp` 走 localhost:4317 collector，本机无 collector 时 span 被静默丢弃，且 Otlp 不写 Serilog .log 文件。需要一种"无需 collector、可直接落盘查看"的观察方式。
- 方案: 新增 File 导出器 `Telemetry:Tracing:Exporter=File`，把已结束的 span 以 JSON Lines 追加写入本地滚动文件。
  - `Tracing/FileActivityExporter.cs`（`BaseExporter<Activity>`）：`{LogDirectory}/traces-yyyyMMdd.jsonl` 按本地日期滚动；每行一个 span（ts / duration_ms / name / kind / trace_id / span_id / parent_span_id / status / service / tags）；线程安全（Export 可能来自后台批次线程，统一锁）；跨日先 Flush 旧文件再开新 writer；Dispose 冲刷关闭。
  - `TelemetryTracingOptions`：`TracingExporterKind` 增加 `File=3`、新增 `LogDirectory`（默认 `logs/traces`，可被环境变量 `Telemetry__Tracing__LogDirectory` 覆盖）；`Resolve` 同步解析。
  - `TelemetryServiceCollectionExtensions.AddNitroTelemetry(IConfiguration,...)`：`Exporter=File` 分支挂 `SimpleActivityExportProcessor(FileActivityExporter)`。
  - 与 Serilog 关系：File 导出器独立于 Serilog（span 是结构化追踪数据，不是日志条目），写 `logs/traces/` 不污染 `logs/nitrogateway-.log`；`logs/` 已在 .gitignore，不会入库。
- 改动文件:
  - `src/NitroGateway.Telemetry/`：Tracing/FileActivityExporter.cs（新）、Tracing/TelemetryTracingOptions.cs、TelemetryServiceCollectionExtensions.cs、NitroGateway.Telemetry.csproj
  - `tests/NitroGateway.UnitTests/Telemetry/`：TelemetryTracingOptionsTests（+2）、TelemetryServiceCollectionExtensionsTests（+1）
- 验证:
  - `dotnet build src/NitroGateway.Telemetry/NitroGateway.Telemetry.csproj` 0 错误 0 警告；Telemetry 过滤器测试 14 通过；全量 `dotnet test`（重定向输出 + `--filter !~DesktopThemeTests`）**633 通过 / 0 失败**。
  - 冒烟（`Telemetry__Tracing__Exporter=File` + 临时 `LogDirectory` + 带设备 DB 启动 Webapi）：20s 内写出 `traces-20260819.jsonl`，50 条 span 覆盖 8 个名字（CollectRound/CollectDevice/ReadDevice/Pipeline/Dispatch/Forward/SqliteWrite/MqttPublish），含 trace_id/parent_span_id/service/tags，与设备采集+转发链路一致。
- 后续: 生产仍以 Otlp 接 jaeger/tempo/otel-collector 为主；File 导出器定位为"单机落盘归档/无 collector 时的本地排查"，可直接用 jq/脚本解析 JSONL。
