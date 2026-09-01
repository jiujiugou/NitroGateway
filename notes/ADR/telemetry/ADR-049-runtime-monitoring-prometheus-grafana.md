# ADR-049: 运行时监控方案——Prometheus + Grafana + prometheus-net.DotNetRuntime

- 日期: 2026-08-15 | 状态: 已实施
- 来源: 用户要求「监控程序运行情况」，选定路线 2（业务 + 运行时指标统一进 /metrics）
- 关联: ADR-009（OTel 预留入口，暂不启用）；ADR-025（Ingest）

## Context

/metrics（prometheus-net）目前只上报业务指标（采集/转发/设备/MQTT/磁盘/告警等），缺内存/CPU/GC 运行时指标；无法监控网关与中心「运行情况」。目标：一套本地监控栈，把业务 + 运行时收进一块 Grafana 面板，及格线转成 Prometheus 告警。

## Decision

- D1 新增依赖 prometheus-net.DotNetRuntime：自动把 System.Runtime EventCounters 汇入 prometheus-net 全局注册表，/metrics 一次暴露，不改业务指标契约。静态字段强引用保活（内部 DotNetRuntimeStatsCollector 的 Finalize 为空、无自动释放路径，被 GC 回收会停采）；StartCollecting() 无幂等守卫，每进程只调一次。
- D2 架构：gateway:5100/metrics + ingest:5200/metrics → Prometheus（scrape + 告警 rules）→ Grafana「运行情况」面板；Webapi /healthz /readyz → Prometheus 探针（blackbox，存活/就绪）。
- D3 采集与展示：新增 docker-compose.monitoring.yml（prometheus + grafana，加入主 compose 网络）+ tools/monitoring/ 下 prometheus.yml / rules.yml / grafana provisioning（datasource + dashboard json）。
- D4 桌面端（WPF）不纳入本方案：本机交互工具，用 dotnet-counters + 自带状态页盯（见 2026-08-15 worklog 桌面定位结论）。
- D5 告警规则用实际暴露指标名（prometheus-net.DotNetRuntime 4.4.1 实测）：内存超标 dotnet_total_memory_bytes > 500MiB for 10m、GC 占比 dotnet_gc_pause_ratio > 0.05 for 5m、采集失败 increase(nitro_collection_total{status="failure"}[5m]) > 0、采集拖周期 p99(nitro_collection_duration_ms) > 900、转发背压 nitro_buffer_backlog > 0 for 5m、MQTT 断连 nitro_mqtt_state != 2 for 1m、磁盘告急 nitro_disk_free_bytes < 1GiB for 5m、落库失败 increase(nitro_store_write_failures_total[5m]) > 0、探针失败 probe_success{job="nitro-probes"} == 0 for 2m。

## Alternatives

- 路线 1：业务指标单独、运行时另起端口暴露：两块面板、两套配置，复杂。
- 路线 2：业务 + 运行时统一进 /metrics（选定）：一次暴露、一块面板。

## Rationale

统一进 /metrics 一次暴露、Grafana 一块面板收全部「运行情况」，及格线转 Prometheus 告警；桌面本机交互工具不常驻服务化，用 dotnet-counters 盯即可。

## Consequences

- /metrics 增加 20 个 dotnet_* 运行时指标（实测名）；新增监控 compose/配置。
- 不改现有 /metrics 契约、不升级/降级现有包；桌面端不动。
