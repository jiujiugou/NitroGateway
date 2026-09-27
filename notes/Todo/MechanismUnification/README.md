# 机制统一待办（Mechanism Unification）

> 所属：[待办事项](../README.md) › MechanismUnification
>
> 目标：把**通用机制**收敛成一份，领域只保留**纯策略**；消除"同一机制被多个领域各造一遍"的散弹式修改。
> 背景：7 个领域（协议接入/采集/转发/命令回写/设备管理/告警/设备健康）+ 通用域，机制无单一归属 → 复制 + 漂移。

## 判据

> 差异能用**参数/策略**表达 → 统一；差异是**领域语义** → 不统一。

## 清单

### A. 完全统一（差异是参数）

| # | 机制 | 涉及领域/模块 | 优先级 | 状态 |
|---|------|--------------|--------|------|
| [01](01-http-client.md) | HTTP 访问 | 转发 / Webapi / Desktop | 高 | 待办 |
| [02](02-resilience-pipeline.md) | 重试/退避/超时管线 | 协议接入 / 转发 / 传输(MQTT·HTTP) | 高 | ✅ 已完成 |
| [03](03-error-classifier.md) | 错误分类（异常→ErrorCategory） | 全部（尤以协议驱动最散） | 中 | ↩ 撤回（ROI 低，待驱动统一时再做） |
| [04](04-ttl-cache.md) | TTL 缓存（双检+失效） | 告警 / 设备管理 | 中 | ✅ 已完成 |
| [05](05-json-options.md) | JSON 序列化配置 | 转发 / 命令回写 / 传输 | 低 | 待办 |
| [06](06-idempotency-store.md) | 幂等键存储 | 命令回写（含跨重启正确性洞） | 高 | 待办 |

### B. 只统一底层原语（差异含语义，不能合并状态机）

| # | 原语 | 复现在 | 优先级 | 状态 |
|---|------|--------|--------|------|
| [07](07-threshold-counter.md) | 阈值化连续计数 → 迁移 | 设备健康 / 传输(HTTP) / 采集(熔断) | 中 | 待办 |
| [08](08-bounded-consumer.md) | 有界队列 + 消费循环 + 停机排空 | 采集 / 告警 / 转发 / MQTT入站 / SignalR | 中 | 待办 |
| [09](09-listener-set.md) | 监听器扇出（fire-and-forget + 异常隔离） | 设备健康 / 传输 | 低 | 待办 |

### C. 不统一（语义不同，强行合并会错）

- **熔断（门控）vs 设备健康判定 vs 连接生命周期**：`CircuitBreaker` 明确只保护执行、不判定故障（`src/NitroGateway.Collection/Resilience/CircuitBreaker.cs:7`）。三者状态集与迁移语义不同。
- **告警 Duration 去抖 vs 值管道死区/降频**：看"持续时长超限" vs 看"变化幅度/采样率"，时间语义不同。
- **命令幂等 vs 告警去重**：一个必须跨重启，一个只在活动期去重。
- **DB outbox vs 内存队列**：耐久性要求不同，是两类东西。

## 落地形态

新增通用机制层（候选名 `NitroGateway.Primitives`，或并入 `Shared`/`Transport`）：
`ResiliencePipelineFactory` · `ErrorClassifier` · `TtlCache<K,V>` · `BoundedConsumer<T>` · `IIdempotencyStore` · `ListenerSet<T>`。
领域侧只注入**策略对象**（阈值、退避参数、ShouldHandle、状态集），机制只此一份。

## 建议顺序

01 HTTP → 02 Resilience → 03 错误分类 → 06 幂等存储 → 04/08 → 其余。

## 相关

- 已落地示范：[ADR-075](../../ADR/forwarder/ADR-075-mqtt-reconnect-polly.md)（MQTT 重试/退避外包 Polly）
- 待补：机制统一的 ADR（本清单完成一项后按需补）
