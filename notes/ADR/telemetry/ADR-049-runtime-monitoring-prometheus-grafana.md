# ADR-049: 运行时监控方案——Prometheus + Grafana + prometheus-net.DotNetRuntime（2026-08-15）

- 日期: 2026-08-15 | 状态: 已实施 | 来源: 用户要求「监控程序运行情况」，选定路线 2（业务 + 运行时指标统一进 /metrics）
- 关联: ADR-009（OTel 预留入口，暂不启用）；ADR-025（Ingest）；Telemetry/NitroMetrics

## 背景与目标
- `/metrics`（prometheus-net）目前只上报业务指标（采集/转发/设备/MQTT/磁盘/告警等），缺内存/CPU/GC 运行时指标；
- 目标：一套本地监控栈，把网关与中心的「运行情况」（业务 + 运行时）收进一块 Grafana 面板，及格线转成 Prometheus 告警；
- 桌面端（WPF）不纳入本方案：本机交互工具，用 dotnet-counters + 自带状态页（F-46）盯（见 2026-08-15 worklog 桌面定位结论）。

## 方案
```
gateway:5100/metrics ─┐
ingest:5200/metrics  ─┤─> Prometheus（scrape + 告警 rules）─> Grafana「运行情况」面板
Webapi /healthz /readyz ─> Prometheus 探针（存活/就绪）
```
- 运行时指标：新增依赖 `prometheus-net.DotNetRuntime`（自动把 System.Runtime EventCounters 汇入 prometheus-net 全局注册表，/metrics 一次暴露，无需改业务指标契约）。
- 业务指标：已有 NitroMetrics，无改动。
- 采集与展示：新增 `docker-compose.monitoring.yml`（prometheus + grafana，加入主 compose 网络）+ `tools/monitoring/` 下 prometheus.yml / rules.yml / grafana provisioning（datasource + dashboard json）。

## 改动清单（实施时）
1. `src/NitroGateway.Telemetry/NitroGateway.Telemetry.csproj`：+ `prometheus-net.DotNetRuntime`（新增依赖，G1）
2. `TelemetryServiceCollectionExtensions.cs`：注册 DotNetRuntimeStats（一行，不改变现有 /metrics 契约）
3. 新增 `docker-compose.monitoring.yml`：prometheus + grafana 服务（复用主 compose 网络）
4. 新增 `tools/monitoring/prometheus.yml` + `rules.yml`（及格线告警）
5. 新增 `tools/monitoring/grafana/provisioning/datasources + dashboards`（预置「运行情况」面板 json）
6. 测试：Telemetry 注册冒烟（host 构建后 /metrics 含 `dotnet_` 前缀指标）+ UnitTests 基线不回归

## 及格线 → 告警规则（已按实际暴露指标名落地，见 tools/monitoring/rules.yml）
| 告警 | 表达式（实际指标名） | 说明 |
| --- | --- | --- |
| 内存超标 | `dotnet_total_memory_bytes > 500MiB` for 10m | 进程已分配内存总量，占位阈值按 soak 基线校准 |
| GC 占比高 | `dotnet_gc_pause_ratio > 0.05` for 5m | 0~1 的暂停占比，超过 5% |
| 采集失败 | `increase(nitro_collection_total{status="failure"}[5m]) > 0` | 持续 5 分钟有失败 |
| 采集拖周期 | `histogram_quantile(0.99, rate(nitro_collection_duration_ms_bucket[5m])) > 900` | p99 拖过 1s 采集周期 |
| 转发背压 | `nitro_buffer_backlog > 0` for 5m | 缓冲持续积压 = 下游堵 |
| MQTT 断连 | `nitro_mqtt_state != 2` for 1m | 状态枚举 2=Connected |
| 磁盘告急 | `nitro_disk_free_bytes < 1GiB` for 5m | 占位阈值，按现场磁盘校准 |
| 落库失败 | `increase(nitro_store_write_failures_total[5m]) > 0` | |
| 探针失败 | `probe_success{job="nitro-probes"} == 0` for 2m | blackbox 对 /healthz /readyz 的存活/就绪 |

> 运行时指标实际命名（prometheus-net.DotNetRuntime 4.4.1，已从运行进程实测）：`dotnet_total_memory_bytes`、
> `dotnet_gc_pause_ratio`、`dotnet_gc_heap_size_bytes`、`dotnet_gc_collection_count_total`、
> `dotnet_threadpool_num_threads`、`dotnet_threadpool_queue_length`(histogram)、`dotnet_exceptions_total`、
> `dotnet_contention_total`、`dotnet_sockets_*`、`dotnet_jit_*`、`dotnet_build_info` 等 20 个 `dotnet_*` 指标；
> 与 ADR 草案中暂定的 `dotnet_process_working_set_bytes`/`dotnet_gc_time_in_gc` 不一致，rules.yml 已用实际名。

## 实施记录（2026-08-15）
- 三问：为什么做=业务指标不含内存/CPU/GC，无法监控网关与中心运行情况；验收=`/metrics` 一次暴露运行时指标、
  compose 起监控栈、rules 告警用真实指标名、UnitTests 不回归；不做=运行问题只能靠 dotnet-counters 现场查。
- G1 确认：新增依赖 `prometheus-net.DotNetRuntime`（不升级/降级现有包）、新增监控 compose/配置、
  不改现有 `/metrics` 契约、桌面端不动。
- 代码改动（`src/NitroGateway.Telemetry` + `src/NitroGateway.Ingest`）：
  - `NitroGateway.Telemetry.csproj`：+ `prometheus-net.DotNetRuntime`（解析 4.4.1，与 prometheus-net 8.2.1 兼容）。
  - `TelemetryServiceCollectionExtensions.cs`：`AddNitroTelemetry()` 内 `StartRuntimeStats()` →
    `DotNetRuntimeStatsBuilder.Default().StartCollecting()`（争用+线程池+GC+JIT+网络+异常，默认 Counters 低开销）；
    静态字段 `_runtimeStats` 强引用保活（内部 `DotNetRuntimeStatsCollector` 的 `Finalize` 为空、无自动释放路径，
    若被 GC 回收会停采）；`StartCollecting()` 无幂等守卫，每进程只调一次（Webapi/Ingest 独立进程各自调用）。
  - `src/NitroGateway.Ingest/Program.cs`：+ `AddNitroTelemetry()`，让中心 ingest 的 `/metrics` 也带 `dotnet_*`。
- 监控配置（`docker-compose.monitoring.yml` + `tools/monitoring/`）：
  - prometheus:v2.53.0（9090）、blackbox:v0.25.0、grafana:11.3.0（3000，admin/admin123 开发默认，生产环境变量覆盖）；
    注释写明两种启动方式（`-f` 合并主栈共享默认网络 / `-p nitrogateway` 同项目名复用网络）。
  - `prometheus.yml`：scrape `gateway:5100/metrics`、`ingest:5200/metrics`；nflask probe 走 blackbox 探
    gateway/ingest 的 `/healthz` 与 `/readyz`。
  - `rules.yml`：9 条告警（见上表，真实指标名）；`blackbox.yml`：http_2xx 模块。
  - `grafana/provisioning/`：datasource（uid=`prometheus`）+ dashboard.yml +「运行情况」面板 json（16 面板）。
- 测试：基线不回归；另修复 2 处既有竞态 flake（见 worklog 2026-08-15 实施段）。

## 验证（实施后）
- `dotnet build NitroGateway.slnx` 0 错误（警告均为既有）；`dotnet test tests/NitroGateway.UnitTests --no-build`
  604/604 通过（基线不回归）。
- Webapi 冒烟：临时 DB 启动后 `curl localhost:5100/metrics` 含 20 个 `dotnet_*` + 15 类 `nitro_*` 业务指标，
  已实测 `dotnet_total_memory_bytes`、`dotnet_gc_pause_ratio` 等；进程/临时 DB/logs/PID 已清理。
- `docker compose -f docker-compose.monitoring.yml up` 后：`curl localhost:5100/metrics` 可见 `dotnet_` 前缀；
  Grafana :3000「运行情况」面板同时显示业务 + 运行时指标；rules.yml 告警生效。
- 未提交 git（由用户执行）。

## G1（行为变更）
- 新增依赖 `prometheus-net.DotNetRuntime`（不升级/降级现有包）；
- 新增监控 compose/配置文件，不改应用行为、不改现有 /metrics 契约；桌面端不动。
