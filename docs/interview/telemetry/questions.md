# Telemetry 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 9 组 46 题；参考答案见 `answers.md`。

---

## 一、模块定位与整体架构

**Q1.1 ★** Telemetry 模块只有 4 个源文件，它的职责边界是什么？哪些事明确不在它范围内（HTTP 暴露 / 日志 / 健康检查）？

**Q1.2 ★★** `AddNitroTelemetry` 是空实现（直接 `return services`），为什么注册后指标依然能工作？prometheus-net 的"默认注册表"藏在哪？
代码定位：`TelemetryServiceCollectionExtensions.cs`；`Webapi/Program.cs:116`。

**Q1.3 ★** `/metrics` 端点由谁、在哪一行暴露？Telemetry 模块自身为什么不暴露 HTTP？
代码定位：`Webapi/Program.cs:116`。

**Q1.4 ★★** `/metrics`、`/healthz`、`/readyz` 三个端点都是"观测"，分工有什么区别？抓取方分别是哪类系统？
代码定位：`Webapi/Program.cs:110-116`；`Webapi/HealthChecks/`。

**Q1.5 ★★** 指标定义为什么用静态类静态字段，而不是 DI 单例注入？这种设计的优点和坑分别是什么？
代码定位：`NitroMetrics.cs` 类注释；`TelemetryServiceCollectionExtensions.cs`。

**Q1.6 ★★★** `NitroGateway.Telemetry.csproj` 引用了 `OpenTelemetry` 两次，且所有包都是 `Version="*"`。这有什么问题？当前 `OpenTelemetry` 包真的被用到了吗？
代码定位：`NitroGateway.Telemetry.csproj:10-13`；全仓库搜索 `OpenTelemetry` 的使用。

---

## 二、指标类型与命名

**Q2.1 ★★** 9 个指标里哪些是 Counter、哪些是 Gauge、哪些是 Histogram？各自的选型依据是什么？
代码定位：`NitroMetrics.cs` 全部字段。

**Q2.2 ★** 命名规范 `nitro_{领域}_{指标名}_{单位后缀}` 下，`nitro_collection_duration_ms` 和 `nitro_collection_total` 的"单位后缀"分别怎么理解？Prometheus 对 counter 名字的 `_total` 后缀有什么约定？
代码定位：`NitroMetrics.cs:9-12`。

**Q2.3 ★★** `CollectionDurationMs` 的桶边界为什么选 5/10/25/50/100/250/500/1000/2500/5000？用这个 Histogram 怎么算 p99？`_bucket` / `_sum` / `_count` 各是什么？
代码定位：`NitroMetrics.cs:25-32`。

**Q2.4 ★★★** 为什么这里选 Histogram 而不是 Summary 来度量耗时？两者在 Prometheus 跨实例聚合上的本质区别是什么？
代码定位：`NitroMetrics.cs` Histogram 定义。

**Q2.5 ★★★** `collection_total` 带 `device`（Guid 字符串）label，`circuit_breaker_state` 也带。这会造成什么规模问题？多网关同时被抓取时 label 会冲突吗？
代码定位：`NitroMetrics.cs:16-22,34-40`；`DeviceCollector.cs:90,130`。

---

## 三、逐指标解析与上报点

**Q3.1 ★★** 不看代码，说出 9 个指标各自「定义在哪、被谁上报、代表什么」。再打开代码核对，找出哪 2 个指标没有任何上报点。
代码定位：`NitroMetrics.cs`；`rg -n "NitroMetrics\." src`。

**Q3.2 ★★** `collection_total` 的 success / failure 分别在哪两行上报？熔断器 Open 期间设备被跳过，计数会发生什么？这个"不计数"语义合理吗？
代码定位：`DeviceCollector.cs:90,129`；跳过路径 `DeviceCollector.cs:77-82`。

**Q3.3 ★★** `circuit_breaker_state` 在失败（:91）和成功（:130）路径 Set，但熔断跳过期间不 Set。Gauge 的值会怎样？监控端如何持续看到 Open 状态？
代码定位：`DeviceCollector.cs:91,130`。

**Q3.4 ★★** `forward_total` 的 label 声明了 success | failure | deadletter，但代码里只有 success（:109）和 failure（:116,126）上报。deadletter 指标缺失意味着什么？怎么才能监控死信？
代码定位：`NitroMetrics.cs:43-48`；`Forwarder.cs:109,116,126`。

**Q3.5 ★★★** `buffer_backlog` 和 `throttle_batch_size` 在 `ForwardBatchAsync` 末尾 Set（:146-147），每 5 秒才采一次样。你能观察到积压的瞬时峰值吗？这是采样偏差问题还是可接受？
代码定位：`Forwarder.cs:146-147`；`ForwarderEngine.cs` 触发周期。

**Q3.6 ★★** `mqtt_state` 的数值来自 `(int)state`。`MqttConnectionState` 枚举顺序是什么？指标 help 文本里写的顺序对吗？抓取端会不会被误导？
代码定位：`MqttClientWrapper.cs:265`；`MqttConnectionState.cs`；`NitroMetrics.cs:66-72`。

---

## 四、陷阱与缺口
> ADR-009 各条目已于 2026-08-09 修复（P1-1/P1-2/P2-1~P2-4），以下题目转为"设计题"继续考察语义与取舍。

**Q4.1 ★★★** `nitro_collection_duration_ms` 曾是"定义了没人上报"（ADR-009 P1-1，已修复）。现在上报点在哪？为什么选 `CollectOnceAsync` 而不是 `CollectDeviceAsync`？（语义：单轮采集耗时，不是单设备耗时）
代码定位：`NitroMetrics.cs:25-32`；`DeviceCollector.CollectOnceAsync`。

**Q4.2 ★★★** `nitro_devices_online` 与 `nitro_devices_available`（`DeviceCollector.cs` 上报）语义差在哪？ADR-009 P1-2 修复后 online 数在哪里、由谁算的？（提示：HealthMonitor 快照里有什么）
代码定位：`NitroMetrics.cs:77-85`；`DeviceCollector.cs:153`；`DeviceManagement/HealthMonitor`。

**Q4.3 ★★** `NitroMetrics` 现在有几个字段？F-23 曾写「8 个指标」（ADR-009 P2-3 已修复）——这类"定义与文档不同步"的漂移根因是什么？怎么防？
代码定位：`docs/03-功能清单.md:64`；`NitroMetrics.cs`。

**Q4.4 ★★★** `mqtt_state` 的枚举序是什么？help 文本曾写错顺序（ADR-009 P2-2 已修复）——错序会误导抓取端什么？（help 是说明文字，但 Grafana 变量 / 告警描述 / 排障会引用它）
代码定位：`NitroMetrics.cs:66-72`。

**Q4.5 ★★★** `/metrics` 端点没有任何认证。会泄露什么？当前靠什么兜底？如果要加防护有哪些方案？
代码定位：`Webapi/Program.cs:116`；backlog 中 D-06 OT 网络隔离。

---

## 五、Tracing 设计

**Q5.1 ★** 全局只有一个 `ActivitySource`（名字 "NitroGateway"），且业务代码被禁止各自 `new ActivitySource`。为什么？
代码定位：`GatewayActivitySource.cs` 类注释。

**Q5.2 ★** `GatewayActivities` 定义了多少个 Activity 名？分别对应哪个组件 / 方法？
代码定位：`GatewayActivities.cs` 全部常量；`rg "StartActivity" src`。

**Q5.3 ★★** `GatewayActivityTags` 统一了 9 个 Tag Key。为什么用常量而不是各模块写字符串？`error.message` 与 `db.table` 的约定用法？
代码定位：`GatewayActivityTags.cs`。

**Q5.4 ★★★** `Source.StartActivity(...)` 在什么情况下返回 null？所有代码都写 `activity?.` 是为了防什么？现在生产环境这个调用实际返回 null 还是 Activity？（全仓库搜 `AddOpenTelemetry` / `ActivityListener`）
代码定位：`GatewayActivitySource.cs`；`rg "AddOpenTelemetry|ActivityListener" src tests`。

**Q5.5 ★★★** 测试里是怎么"捕获" Activity 的？`ForwarderActivityTests.StartListener` 做了什么？为什么收集的是 `ActivityStopped`？
代码定位：`tests/NitroGateway.UnitTests/ForwarderActivityTests.cs:119-137`。

---

## 六、Activity 状态约定

**Q6.1 ★★** 仓库里 Activity 状态的约定是什么（Ok / Error / 描述）？这个约定从哪次修复开始被显式要求？
代码定位：`Forwarder.cs:60-64` 注释（ADR-001 P2-9）；`ForwarderActivityTests` 类注释。

**Q6.2 ★★** 列出所有「失败路径显式置 Error」的代码位置（Collect / Forward / SqliteWrite / MqttPublish 各在哪几行）。
代码定位：`DeviceCollector.cs:93-94`；`Forwarder.cs:78,118,129,142`；`SqliteMeasurementStore.cs:74-75`；`MqttClientWrapper.cs:151-152,181-182,187-188`。

**Q6.3 ★★★** `ReadDevice` span 从头到尾没有 `SetStatus`。读设备失败时它是什么状态？错误信息在哪个 span 上能看到？这是缺陷还是有意设计？
代码定位：`DeviceReader.cs:44-72`；`DeviceCollector.cs:93-94`。

**Q6.4 ★★★** `CollectDevice` 在熔断 Open 时直接 return，span 是什么状态？为什么"不置状态"反而是合理的？查询时怎么和 Error 区分？
代码定位：`DeviceCollector.cs:77-82`。

**Q6.5 ★★** `Dispatch` 在 Channel 满丢弃数据、缓冲入队失败时依然置 Ok（:83）。它表达的是"编排成功"还是"数据全部落库"？要看数据是否真正落库该看哪个 span？
代码定位：`DataDispatcher.cs:57,83`；`SqliteMeasurementStore.cs:74`。

**Q6.6 ★★** `CollectRound` 为什么总是 Ok（除非取消 / 异常）？子设备采集失败会传导到 `CollectRound` 吗？`Task.WhenAll` 在这里的作用？
代码定位：`DeviceCollector.cs:163-196`。

---

## 七、链路与上下文

**Q7.1 ★★★** 画出一轮完整采集的 Span 树（读取 / 转换 / 分发 / 写库），再画出转发链路的 Span 树。两条链路的交集在哪？
代码定位：8 个 `StartActivity` 位置。

**Q7.2 ★★★** `SqliteWrite` span 的父 span 是谁？注意 `DataDispatcher` 只是 `Post` 进 Channel，真正执行 `WriteAsync` 的是谁？这个异步解耦对 tracing 父子关系意味着什么？
代码定位：`DataDispatcher.cs:61`；`MeasurementWriteHost.cs:54-61`。

**Q7.3 ★★★** `Activity.Current` 在 async/await 下如何流动？`CollectOnceAsync` 用 `Task.WhenAll` 并发采集多台设备时，每个 `CollectDevice` span 的父是谁？
代码定位：`DeviceCollector.cs:171-183`。

**Q7.4 ★★★** 要让这些 Span 出现在 Jaeger / Grafana Tempo 里，需要补什么？逐条列出从"定义 Activity"到"后端可见"的完整链路（SDK、AddSource、exporter、协议、配置）。
代码定位：`GatewayActivitySource.cs`；`NitroGateway.Telemetry.csproj:12-13`。

---

## 八、测试与验证

**Q8.1 ★★** 为什么现有测试只有 `ForwarderActivityTests` 而没有指标测试？prometheus-net 的静态全局注册表给测试带来什么麻烦？
代码定位：`NitroMetrics.cs` 静态字段；`tests/NitroGateway.UnitTests`。

**Q8.2 ★★** 如果要给 `collection_total` 写单测，怎么隔离全局注册表？（提示：`CollectorRegistry` 实例、`Metrics.WithCustomRegistry` 或重置）现有代码支持吗？
代码定位：`NitroMetrics.cs`。

**Q8.3 ★** 手动验证命令：`curl http://localhost:5100/metrics | grep nitro_`。如何判断一个 counter 的"速率"而不是"总量"？Histogram 的哪些后缀指标会出现在输出里？
代码定位：`Webapi/Program.cs:116`。

**Q8.4 ★★★** 诊断题：一台设备 PLC 掉线 10 分钟，用指标 + span 描述你会在监控端看到什么（collection_total / circuit_breaker_state / span 状态 / devices_available）。
代码定位：`DeviceCollector.cs:77-95,129-132`。

---

## 九、诊断与开放题

**Q9.1 ★★★** 场景题：MQTT Broker 断线 3 小时再恢复。按时间线描述 `mqtt_state`、`forward_total`、`buffer_backlog`、`throttle_batch_size` 的变化，以及恢复后积压如何排空。
代码定位：`MqttClientWrapper.cs:265`；`Forwarder.cs:146-147`；`ForwardingThrottle.cs`。

**Q9.2 ★★★** 你现在是负责人，要给 Telemetry 模块做一次"闭环优化"，按优先级列出 3-5 件事并说明理由（已知缺口见 Q4.1 / Q4.2 / Q3.4 / Q4.4 / Q4.5）。
代码定位：`notes/ADR/ADR-009-telemetry-observability-gaps.md`。

**Q9.3 ★★★** 新增一个"告警触发次数"指标（label: ruleId, severity），按仓库规范写出完整步骤（定义 → 上报 → 测试 → 文档），并指出与现有 9 个指标的一致性要求。
代码定位：`NitroMetrics.cs` 命名规范；`docs/03-功能清单.md` F-23。

**Q9.4 ★★★** 可观测性三大支柱（logging / metrics / tracing）在本仓库的现状各是什么水平？缺口分别在哪？如果要给生产环境上线可观测性，第一刀砍在哪？
代码定位：`Webapi/Program.cs` Serilog；`NitroMetrics.cs`；`GatewayActivitySource.cs`。

**Q9.5 ★★★** 陷阱复盘：把本模块所有"定义与实现不一致"的点列全（哑火指标、deadletter 标签、help 文本、F-23、csproj 重复引用、无监听器），并说出每题在面试中如何展开讲。
代码定位：见各题。
