# ADR-057: 追踪 File 导出器（JSONL 落盘本地观察）

- 日期: 2026-08-19 | 状态: 已实施
- 背景: ADR-056 接入 OTLP/Console 后，用户问「追踪在哪里观察？日志里没看出来」

## Context

默认 Exporter=Otlp 走 localhost:4317 collector，本机无 collector 时 span 被静默丢弃，且 Otlp 不写 Serilog .log 文件。需要一种「无需 collector、可直接落盘查看」的观察方式。

## Decision

- D1 新增 File 导出器 Telemetry:Tracing:Exporter=File：把已结束 span 以 JSON Lines 追加写入本地滚动文件 {LogDirectory}/traces-yyyyMMdd.jsonl（按本地日期滚动）；每行一个 span（ts / duration_ms / name / kind / trace_id / span_id / parent_span_id / status / service / tags）；线程安全（Export 可能来自后台批次线程，统一锁）；跨日先 Flush 旧文件再开新 writer；Dispose 冲刷关闭。
- D2 TracingExporterKind 增加 File=3、新增 LogDirectory（默认 logs/traces，可被环境变量 Telemetry__Tracing__LogDirectory 覆盖）；Resolve 同步解析。
- D3 与 Serilog 关系：File 导出器独立于 Serilog（span 是结构化追踪数据，不是日志条目），写 logs/traces/ 不污染 logs/nitrogateway-.log；logs/ 已在 .gitignore，不会入库。
- D4 定位：File 导出器为「单机落盘归档/无 collector 时的本地排查」，可直接用 jq/脚本解析 JSONL；生产仍以 Otlp 接 jaeger/tempo/otel-collector 为主。

## Alternatives

- 把 OTLP span 打进 Serilog 日志：污染日志文件、丢失结构化，不可取。
- 不做本地观察方式：用户无法看到追踪。

## Rationale

span 是结构化追踪数据，独立 JSONL 落盘便于 jq/脚本解析与归档；按日滚动避免单文件无限增长；与 Serilog 解耦不污染业务日志。

## Consequences

- Exporter=File 时写 logs/traces/traces-*.jsonl（已 gitignore）；生产仍以 Otlp 为主。
