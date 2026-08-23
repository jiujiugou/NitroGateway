# Telemetry 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案"读出来"再背一遍。
> 代码是唯一事实来源：指标字段数以 `NitroMetrics` 为准（9 个，F-23 已同步）；ADR-009 条目已于 2026-08-09 修复。

---

## 一、模块定位与整体架构

**Q1.1 职责边界**
- 做：定义全局 Prometheus 指标（`NitroMetrics`）、统一 Activity 定义（Source / 名称 / Tag）、提供 DI 注册入口（`AddNitroTelemetry`）。
- 不做：HTTP 暴露（`/metrics` 由 Webapi 的 `app.MapMetrics()` 做）、日志（Serilog 由 Webapi/Host 配置）、健康检查（`/healthz` `/readyz` 是 Webapi 的 HealthChecks）、指标采集器（prometheus-net 库负责抓取端协议）。
- 一句话：Telemetry 是"定义与约定"模块，接线（端点）和消费（上报点）都在别的模块。

**Q1.2 空注册为什么还能工作**
- `Metrics.CreateCounter(...)` 等全部走 prometheus-net 的**静态默认注册表**（`Metrics.DefaultRegistry`），不依赖 DI 容器。
- `AddNitroTelemetry()`（无参）仍只注册指标：prometheus-net 静态注册表无需 DI，`MapMetrics()` 抓的就是默认注册表；`AddNitroTelemetry(IConfiguration, serviceName)` 重载（ADR-056）额外启用 OpenTelemetry 追踪执行层。
- 相关测试：`TelemetryServiceCollectionExtensionsTests`（TracerProvider 注册 / dormant 行为）、`TelemetryTracingOptionsTests`（配置解析）。

**Q1.3 /metrics 暴露点**
- `src/NitroGateway.Webapi/Program.cs:116` 的 `app.MapMetrics()`（来自 prometheus-net.AspNetCore）。
- Telemetry 模块不引 ASP.NET Core 框架（csproj 只有库包），保持纯定义；端点归属 Webapi 是分层决策。

**Q1.4 三个端点的分工**
- `/metrics`：Prometheus 文本格式观测数据，抓取方是 Prometheus/Grafana agent，供告警与大盘。
- `/healthz`：存活探针（K8s liveness），只要进程活着就 200。
- `/readyz`：就绪探针（K8s readiness），`db` + `mqtt` 标签检查通过才算 Ready（`Program.cs:110-111`）。
- 都是"观测"但受众不同：探针是调度决策，metrics 是监控决策。

**Q1.5 静态类设计的优缺点**
- 优点：零依赖随处可上报（`DeviceCollector`/`Forwarder`/`MqttClientWrapper` 直接引用）；没有 DI 生命周期问题；与 prometheus-net 静态注册表天然匹配；命名集中一处可审查。
- 坑：全局可变状态 → 单测互相污染（Q8.1）；无法按实例隔离（多租户/多注册表场景要改造成 `WithCustomRegistry`）；隐藏依赖（不通过构造函数体现）。

**Q1.6 csproj 问题**
- 重复引用已在 ADR-009 P2-4 去重；遗留 `Version="*"` 浮动版本：每次 restore 可能拉到不同版本，构建不可复现（全仓库多处也这么用）。
- 追踪执行层已落地（ADR-056）：新增 `OpenTelemetry.Extensions.Hosting` + `Exporter.Console` + `Exporter.OpenTelemetryProtocol`（锁 1.17.0，与既有核心解析版本对齐），`AddOpenTelemetry().WithTracing(...)` 已接线、默认 Otlp 导出 → 裸 `OpenTelemetry` 核心包**已被用到**；建议核心包同样锁版。
- File 导出器（ADR-057）：`Tracing/FileActivityExporter.cs` 把 span 落盘 `{LogDirectory}/traces-yyyyMMdd.jsonl`（默认 `logs/traces`），无需 collector，解决"没后端看不到 span"的本地观察。

---

## 二、指标类型与命名

**Q2.1 类型盘点**
- Counter（2）：`nitro_collection_total`、`nitro_forward_total` —— 计数只增，用 `rate()` 算吞吐。
- Gauge（6）：`nitro_circuit_breaker_state`、`nitro_buffer_backlog`、`nitro_mqtt_state`、`nitro_devices_online`、`nitro_devices_available`、`nitro_disk_free_bytes` —— 当前值可升可降。（`nitro_throttle_batch_size` 已于 2026-08-22 随 AIMD 节流删除）
- Histogram（1）：`nitro_collection_duration_ms` —— 分布统计，算分位数。
- 选型依据：事件次数用 Counter；瞬时状态用 Gauge；耗时分布用 Histogram。

**Q2.2 命名规范**
- `nitro_collection_duration_ms`：`ms` 是单位后缀，量纲写进名字避免抓取端猜单位。
- `nitro_collection_total`：counter 的 Prometheus 约定后缀 `_total`（`rate(nitro_collection_total[5m])` 才成立；`_total` 是自动约定的基础指标名）。
- 规范原文：`nitro_{领域}_{指标名}_{单位后缀}`（`NitroMetrics.cs:9-12` 类注释）。

**Q2.3 Histogram 桶设计**
- 桶 5→5000ms 覆盖 1s 采集周期的典型耗时范围：正常 <100ms，慢设备到秒级，>5000ms 说明一轮超周期（采集堆积前兆）。
- 输出：`_bucket{le=...}` 累积计数、`_sum` 总和、`_count` 样本数。
- p99：`histogram_quantile(0.99, sum(rate(nitro_collection_duration_ms_bucket[5m])) by (le))`。

**Q2.4 Histogram vs Summary**
- Summary 在客户端算分位数，**无法跨实例聚合**（sum 没意义）。
- Histogram 上传桶计数，分位数由 PromQL `histogram_quantile` 在服务端算，可跨网关聚合；代价是桶数量与带宽。
- 网关是分布式多实例场景，选 Histogram 正确。

**Q2.5 基数问题**
- `device` label 是 Guid 字符串，基数 = 设备数；设备多时指标基数膨胀（每个序列占内存/抓取带宽）。
- 多网关场景：每台网关的 device label 相同但**不是同一设备**，聚合查询会串数据 → 必须靠 Prometheus 抓取自动附加的 `instance`/`job` 标签区分网关维度（Q9.3 展开）。

---

## 三、逐指标解析与上报点

**Q3.1 全景**
- 定义：`NitroMetrics.cs` 静态字段（Counter 7 / Gauge 6 / Histogram 1）。
- 上报：`DeviceCollector.cs`（采集/熔断/落库失败）；`Forwarder.cs:104,110,125,143`（success/failure/BufferBacklog）；`SqliteForwardOutbox.cs:431`（dropped）；`MqttClientWrapper.cs`（mqtt_state）。
- 哑火 2 个：`nitro_collection_duration_ms`、`nitro_devices_online`（只有定义无写入方）。

**Q3.2 collection_total 语义**
- failure 在 `DeviceCollector.cs:90`（读失败），success 在 `:129`（整台设备走完）。
- 熔断 Open 时 `CollectDeviceAsync` 开头直接 return（`:77-82`），**不计数** → 熔断期该设备计数停滞、`rate` 归零，可反推"被熔断跳过"。
- 合理：跳过既不是成功也不是失败；但监控要配合 `circuit_breaker_state` 看，否则会把"熔断"误读为"设备消失"。

**Q3.3 circuit_breaker_state**
- 失败路径 `:91`、成功路径 `:130` 各 Set 一次；熔断期间不 Set。
- Gauge 是"最后写入值"语义，保持 1（Open）不变 → 大盘上能持续看到 Open，无需每轮重写。
- 隐患：进程重启后未采集前该序列不存在（`_created`/首次抓取后才出现），需要结合 `collection_total` 判断。

**Q3.4 deadletter 标签缺失**
- ~~历史遗留~~：原声明 `status ∈ {success, failure, deadletter}` 但无人上报；2026-08-22 转发简化（删死信，改重试超限即丢弃）后标签改为 `{success, failure, dropped}`，丢弃点在 `SqliteForwardOutbox.cs:431` 上报 `WithLabels("dropped").Inc()`。
- 现在：`dropped` 已可观测，`forward_total{status="dropped"}` 上升即"数据不可达被放弃"，可做告警。

**Q3.5 采样偏差**
- `Forwarder.cs:143` 每轮（5s）末尾采样一次，是**周期性采样**不是事件驱动 → 抓不到两轮之间的瞬时峰值。
- 可接受性：积压是慢变量（断线期间持续堆积），5s 采样足够告警；若要看精确峰值需在 `EnqueueAsync` 侧打点。
- 另注意：`BufferBacklog` 取值是 `_buffer.Count` 内存计数，若缓冲在另一进程/库中则不准（当前单进程可接受）。

**Q3.6 mqtt_state 映射**
- 枚举序（`MqttConnectionState.cs`）：Disconnected=0, Connecting=1, Connected=2, Reconnecting=3, Faulted=4。
- `MqttClientWrapper.cs:265` 的 `SetState` 写 `(int)state`，数值本身正确。
- help 文本曾写错顺序（ADR-009 P2-2 已修复）：现为 "0=Disconnected 1=Connecting 2=Connected 3=Reconnecting 4=Faulted"，与枚举序一致 → 抓取端/告警描述引用 help 不会误读。

---

## 四、陷阱与缺口

**Q4.1 collection_duration_ms（ADR-009 P1-1 已修复）**
- 现状：`CollectOnceAsync` 用 Stopwatch 计时整轮并行采集，`finally` 中 `CollectionDurationMs.Observe(...)`（`DeviceCollector.cs`）。
- 语义：单轮所有设备并发采集总耗时，不是单设备耗时；不含设备列表获取。
- 相关测试：`CollectOnceAsync_ReportsOnlineAndDurationMetrics`。

**Q4.2 devices_online（ADR-009 P1-2 已修复）**
- 语义差：`devices_available` = 过滤维护模式后的**待采集设备数**；`devices_online` = **健康在线数**（HealthMonitor 快照里 `Status == Online` 的设备数）。
- 上报点：`CollectOnceAsync` 在 `DevicesAvailable.Set` 同处刷新 `DevicesOnline.Set(快照统计)`——Collection 同时持有"可用数"与"在线数"，语义靠注释区分。
- 影响：大盘可算"在线率 = online / 总数"。

**Q4.3 F-23 文档漂移（ADR-009 P2-3 已修复）**
- `NitroMetrics` 9 个字段，F-23 与 `01-盘点.md` 已改为 9 个。
- F-25 的「8 个 Span」与 `GatewayActivities` 常量一致，无漂移。

**Q4.4 mqtt_state help 误导（ADR-009 P2-2 已修复）**
- help 曾写错（"1=Connected 2=Reconnecting 3=Connecting"），已对齐枚举序（Disconnected=0 Connecting=1 Connected=2 Reconnecting=3 Faulted=4）。
- 教训：help 是说明文字，Grafana 变量映射/告警描述会引用它——写错会让"按 help 做映射"的告警错位；数值本身始终以枚举序为准。

**Q4.5 /metrics 无认证**
- 泄露面：指标名 + label（设备 ID、成功/失败量、熔断状态、MQTT 状态、积压量）→ 攻击者可据此推断设备规模与网络健康度。
- 兜底：当前只有 OT 网络隔离预期（backlog D-06 未实施）。
- 可选方案：网络层隔离（独立网段/firewall）、反向代理 Basic Auth、`MapMetrics` 前挂鉴权中间件（注意别把 Prometheus 抓取也挡掉）。

---

## 五、Tracing 设计

**Q5.1 单一 ActivitySource**
- 监听者只 `AddSource("NitroGateway")` 一个名字即可全量采样；各自 new Source 会导致采样配置分散、`AddSource` 列表失控。
- 统一命名也保证 Jaeger/OTel 后端里 service 内 span 同源（`GatewayActivitySource.cs` 注释原文：禁止各模块自行 new ActivitySource）。

**Q5.2 8 个 Span 对照**
- `CollectRound` = `DeviceCollector.CollectOnceAsync`（整轮）
- `CollectDevice` = `DeviceCollector.CollectDeviceAsync`（单设备）
- `ReadDevice` = `DeviceReader.ReadDeviceAsync`（读原始值）
- `Pipeline` = `PointValuePipeline.Process`（值转换）
- `Dispatch` = `DataDispatcher.DispatchAsync`（分发：时序通道+缓冲+事件）
- `Forward` = `Forwarder.ForwardBatchAsync`（转发一轮）
- `SqliteWrite` = `SqliteMeasurementStore.WriteAsync`（时序写库）
- `MqttPublish` = `MqttClientWrapper.PublishAsync`（MQTT 发布）

**Q5.3 Tag 常量**
- 统一 key 保证跨模块可查询（`device.id` 在所有模块写法一致）；业务代码写字符串容易漂移（`deviceId` vs `device_id`）。
- `error.message`：失败时放人类可读原因；`db.table`：标识写入目标表（当前 "measurements"），未来多表可区分。
- 注意：Tag 不用于高基数数据（如每次值），只放身份/计数/原因。

**Q5.4 StartActivity 返回 null 的条件**
- `ActivitySource.StartActivity` 在**没有任何监听者**（`ActivityListener` / OpenTelemetry SDK）时返回 null；`activity?.` 就是防这个。
- 现状（ADR-056/057）：Webapi/Ingest 已注册 `AddOpenTelemetry().WithTracing(...)`（导出器 Otlp/Console/File 三选一）→ **返回真实 Activity 并导出**；`activity?.` 仍防"Enabled=false / Exporter=None 回到 dormant"时返回 null。
- 测试（`ForwarderActivityTests`）仍用 `ActivityListener` 捕获，不依赖生产 TracerProvider。

**Q5.5 测试捕获方式**
- `ForwarderActivityTests.cs:119-137`：`ShouldListenTo` 按 `source.Name == GatewayActivitySource.Name` 过滤；`Sample` 返回 `AllDataAndRecorded`（全量记录）；在 `ActivityStopped` 里收集（span 结束时状态/标签才完整，且 `using` 释放才触发）。
- 断言：成功路径 Ok、失败路径 Error + 原因（4 个用例，见 `ForwarderActivityTests`）。

---

## 六、Activity 状态约定

**Q6.1 约定来源**
- 约定：全成功置 `Ok`；任一失败/异常/提交失败置 `Error` + 描述（`SetStatus(ActivityStatusCode.Error, reason)`）。
- 来源：ADR-001 P2-9（Forwarder 失败路径显式置 Error，注释在 `Forwarder.cs:60-62`），已在各模块推广。
- 动机：`ActivityStatusCode.Unset`（默认）无法区分"没执行"和"成功"；显式 Ok/Error 让 Jaeger 按状态过滤可靠。

**Q6.2 失败路径清单**
- Collect：`DeviceCollector.cs:93-94`（读失败 Error + error.message）
- Forward：`Forwarder.cs:77`（Dequeue 失败）、`:112`（发布失败）、`:128`（异常）、`:139`（Commit 失败）
- SqliteWrite：`SqliteMeasurementStore.cs:74-75`（异常回滚后 Error + 异常串）
- MqttPublish：`MqttClientWrapper.cs:151-152`（未连接）、`:181-182`（ReasonCode 失败）、`:187-188`（异常）

**Q6.3 ReadDevice 从不 SetStatus**
- 失败时 `ReadDevice` span 保持 **Unset**；错误信息只在**父 span `CollectDevice`** 上（`:93-94`）。
- 定位：`DeviceReader.cs:44-72` 只有 StartActivity + 两个 tag，无任何 SetStatus。
- 判断：属缺陷/半成品——失败路径至少应置 Error（错误归属在子 span 更清晰）；当前靠父 span 兜底，若未来加中间层会丢失。

**Q6.4 熔断跳过 = Unset**
- `CollectDevice.cs:77-82` Open 时直接 return → span Unset。
- 合理：跳过是"本轮未执行"不是失败；Jaeger 里 Unset 与 Error 可区分（`status=unset` vs `status=error`），查询"Error"不会误报熔断。
- 若要更细，可加 tag 标明跳过原因（当前没有）。

**Q6.5 Dispatch 恒 Ok 的语义**
- `DataDispatcher.cs:83` 无条件 Ok：表达"编排完成"（入队动作本身成功），即使 Channel 满丢弃（仅 LogWarning）或缓冲入队失败（仅日志）。
- 数据是否落库要看 `SqliteWrite`（失败会 Error）；事件是否推送是 fire-and-forget，span 不覆盖。
- 结论：Dispatch Ok ≠ 数据全落地，两层 span 要搭配解读。

**Q6.6 CollectRound 不传导子失败**
- `DeviceCollector.cs:185` 全轮完成置 Ok；子设备失败被 `CollectDeviceAsync` 内部吞掉（不抛异常契约）→ `Task.WhenAll` 不会抛 → CollectRound 仍 Ok。
- 设计意图：轮次调度成功 ≠ 每台设备成功；单设备失败看 `CollectDevice` 子树。

---

## 七、链路与上下文

**Q7.1 Span 树**
- 采集链路：`CollectRound` → `CollectDevice` → `ReadDevice` / `Pipeline` → `Dispatch`。
- 写库/推送是异步消费：`SqliteWrite`（MeasurementWriteHost 消费者上下文）、`Forward` → `MqttPublish`（转发引擎上下文）。
- 交集：无直接父子；`Dispatch` 只是把数据投递到两个通道，后续 span 在各自消费者线程里，靠业务关联（device.id / batch）而不是 trace 关联。

**Q7.2 SqliteWrite 的父 span**
- `DataDispatcher.cs:61` 只 `Post` 进 Channel；`MeasurementWriteHost.cs:54-61` 后台循环 `TryRead` 后调 `WriteAsync`。
- 所以 `SqliteWrite` 的 `Activity.Current` 是消费者上下文（通常无父 span），**不是 Dispatch 的子 span**。
- 含义：trace 树不完整；要把写库挂回采集链路需要手动传 trace 上下文（Channel 消息带 `ActivityContext`），当前没有。

**Q7.3 Activity.Current 流动**
- `Activity.Current` 随 `ExecutionContext` 在 async/await 间流动：`CollectRound` 里创建的 Activity 是并发任务的父。
- `DeviceCollector.cs:171-183` 每个 `CollectDeviceAsync` 内部 `StartActivity` 自动取当前 Activity 为父 → 并发采集时多个 `CollectDevice` 并行挂在同一个 `CollectRound` 下（兄弟关系），符合"一轮"的语义。

**Q7.4 接入后端的最小链路**
1. 加 OpenTelemetry SDK + OTLP exporter 包（当前只有裸 `OpenTelemetry` 包，无 SDK/Exporter）。
2. `builder.Services.AddOpenTelemetry().WithTracing(b => b.AddSource(GatewayActivitySource.Name).AddOtlpExporter(o => o.Endpoint = ...))`。
3. 配置 OTLP endpoint（jaeger/tempo/otel-collector）。
4. 启动后 `StartActivity` 返回真实 Activity，span 才落盘。
- 现状（ADR-056/057 已完成）：1/2/3/4 全部落地——SDK+导出器包已加、`WithTracing` 已接（`TelemetryServiceCollectionExtensions`）、`Telemetry:Tracing` 配置段已写（默认 Otlp；`Exporter=None`/`Enabled=false` 回落到 dormant）、`StartActivity` 返回真实 Activity 并导出（冒烟在 Console/File 导出下可见全部 8 个 span）。

**Q7.5 没有 collector 时，span 去哪里观察**
- 三种导出器对应三种观察点：
  1. `Exporter=Otlp`：发往 `Telemetry:Tracing:Endpoint`（默认 localhost:4317）的 jaeger/tempo/otel-collector，在对应 UI 按 service.name / trace_id 查。本机没 collector 时 span 被**静默丢弃**，且 Otlp 不写 Serilog `.log`——这正是"日志里没看出来"的原因。
  2. `Exporter=Console`：span 打印到进程 stdout（Docker 里即容器日志 `docker logs`），本地直接看控制台。
  3. `Exporter=File`（ADR-057）：落盘 `{LogDirectory}/traces-yyyyMMdd.jsonl`（默认 `logs/traces/`），每行一个 span，直接打开或用 jq/脚本解析，无需任何后端。
- 配置：`Telemetry__Tracing__Exporter=File`（+ 可选 `Telemetry__Tracing__LogDirectory`），appsettings 与环境变量皆可；`logs/` 已被 .gitignore，不会入库。
- File 导出器与 Serilog 独立：span 是结构化追踪数据，写 `logs/traces/`，不混入 `logs/nitrogateway-.log`。

---

## 八、测试与验证

**Q8.1 为什么没有指标测试**
- prometheus-net 的 `Metrics.CreateCounter` 写**静态默认注册表**：测试之间、测试与生产代码共享全局状态 → 同名指标重复创建会抛异常，断言也会被其他测试污染。
- 现状只有 `ForwarderActivityTests`（Activity 可隔离捕获）；指标测试需自定义 `CollectorRegistry` 才能隔离，现有 `NitroMetrics` 静态写法不支持注入。

**Q8.2 指标单测的隔离方案**
- prometheus-net 提供 `CollectorRegistry` 实例 + `Metrics.WithCustomRegistry(registry)`（工厂模式）→ 被测代码需从"静态直接引用"改为"可注入的 IMetricFactory"。
- 现有 `NitroMetrics` 是纯静态，**不支持**注入 → 要测就得先做小重构（构造函数/属性注入注册表）。
- 最小成本替代：集成测试里抓 `/metrics` 文本断言序列存在。

**Q8.3 手动验证**
- `curl http://localhost:5100/metrics | Select-String "nitro_"`；counter 看 `rate(nitro_collection_total[5m])` 而不是裸值（裸值含重启前累计）。
- Histogram 输出含 `_bucket{le=...}`、`_sum`、`_count` 三类；Gauge 直接是当前值。

**Q8.4 单设备掉线诊断**
- `collection_total{device=...,status="failure"}` 增长、`rate` 上升；随后熔断 Open：`circuit_breaker_state{device=...}=1`，`collection_total` 该设备**停止增长**。
- span：`CollectDevice`/`ReadDevice` 子树 Error + error.message（`DeviceCollector.cs:93-94`）；熔断后不再有该设备 span（跳过）。
- `devices_available` 不变（只看维护模式过滤，不反映掉线）；在线数因 `devices_online` 哑火看不到 → 需要靠失败率 + 熔断状态组合判断。

---

## 九、诊断与开放题

**Q9.1 Broker 断线 3h 场景**
- 断线瞬间：`mqtt_state` 2→3（Reconnecting，`MqttClientWrapper.cs:265` 的 SetState 触发），抓取端可见。
- 断线期间：`forward_total{status="failure"}` 增长；`buffer_backlog` 持续上升（每 5s 采样）。
- 若最终 Faulted：`mqtt_state=4`；Forwarder 未连接时跳过本轮（`ForwarderEngine` State 检查），backlog 不再增长但也不排空。
- 恢复：`mqtt_state` 回 2；每轮固定 ≤1000 批排水（2026-08-22 删 AIMD，无节流状态变化）；`buffer_backlog` 逐轮下降排空；期间可能有重复投递（QoS1 at-least-once，靠云端幂等兜底）。

**Q9.2 闭环优化排序（参考答案，按影响）**
1. ~~接 OTLP/OpenTelemetry~~ **已完成（ADR-056）**：追踪从"空转"变"可用"（Q7.4）。
2. ~~本地观察通道~~ **已完成（ADR-057）**：File 导出器落盘 JSONL，无 collector 也能看 span（Q7.5）。
3. ~~补 `devices_online` 上报~~ **已完成（ADR-009 P1-2）**：在线率是核心指标（Q4.2）。
4. ~~补 `collection_duration_ms` 上报~~ **已完成（ADR-009 P1-1）**：性能回归可告警（Q4.1）。
5. ~~补 deadletter 计数~~ **已完成（ADR-009 P2-1，2026-08-22 改名 dropped）**：数据不可达要能告警（Q3.4）。
6. ~~修 help 文本 + F-23 文档 + csproj 去重~~ **已完成（ADR-009 P2-2/P2-3/P2-4）**：低成本一致性（Q4.4/Q4.3/Q1.6）。
- 剩余：/metrics 鉴权（视网络隔离计划排期）；生产 OTLP 端点接入（当前默认 localhost:4317，需指到 jaeger/tempo/otel-collector；本机排查先用 `Exporter=File`）。

**Q9.3 新增指标流程**
1. `NitroMetrics.cs` 加 `Counter AlertTotal = Metrics.CreateCounter("nitro_alarm_total", "...", new CounterConfiguration { LabelNames = ["rule", "severity"] })`（命名规范 + 低基数 label）。
2. 告警触发处 `WithLabels(ruleId, severity).Inc()`。
3. 单测（需先解决注册表隔离 Q8.2）或集成测试断言 `/metrics` 出现 `nitro_alarm_total`。
4. 更新 `docs/03-功能清单.md` F-23 数量与名称。
- 一致性要求：命名 `nitro_{领域}_...`、单位后缀、label 基数控制、帮助文本与枚举/实际语义一致。

**Q9.4 三大支柱现状**
- logging：Serilog（控制台+文件+结构化）——最成熟，基本可用。
- metrics：定义 14 个（Counter 7 / Gauge 6 / Histogram 1）、实际接线除 duration/online 外均可用（Q4.1/Q4.2）、端点无鉴权——可用。
- tracing：8 个 Span 定义完整 + 状态约定好，**已启用执行层**（Webapi/Ingest 默认 Otlp 导出，ADR-056；无 collector 本地观察用 `Exporter=File` 落盘 JSONL，ADR-057；`Exporter=None`/`Enabled=false` 可关）——从"最弱"变为可用，生产需配好 OTLP 端点。
- 第一刀已落下：OTLP 已接、tracing 生效，形成"日志/指标/追踪"三件套闭环；下一步是端点鉴权与生产 collector 落地。

**Q9.5 陷阱复盘清单**
- ~~哑火指标~~ 已修复：`collection_duration_ms`、`devices_online`（ADR-009 P1-1/P1-2，Q4.1/Q4.2）。
- ~~定义未用~~ 已修复并演进：`forward_total` 原 deadletter label 无人上报 → 2026-08-22 转发简化后改为 `dropped`（重试超限丢弃），`SqliteForwardOutbox.cs:431` 已上报（Q3.4）。
- ~~文本错误~~ 已修复：`mqtt_state` help 顺序（ADR-009 P2-2，Q4.4）。
- ~~文档漂移~~ 已修复：F-23 8 vs 9（ADR-009 P2-3，Q4.3）。
- ~~工程问题~~ 部分已修复：csproj 重复引用已去重（P2-4）；`Version="*"` 遗留（Q1.6）。
- ~~架构半成品~~ 已修复：无监听者 → 追踪空转（ADR-056，Q5.4/Q7.4）。
- ~~观察无门~~ 已修复：无 collector 时 span 无处可看（ADR-057，Q7.5）。
- 面试展开套路：先说"定义/约定"再说"实际接线"，用"我发现 + 代码位置 + 影响 + 修复方向"四段式讲，每个缺口都能落到一行代码。
