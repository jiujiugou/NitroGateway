# ADR-075: MQTT 重试/退避外包 Polly（删除自写退避算法）

> **摘要**: MQTT 重连的重试/退避策略外包给 Polly（指数退避 + 抖动 + MaxDelay 封顶），删除自写 `ComputeBackoffDelayMs` 与手写 attempt 循环；MQTTnet 5 无 ManagedClient，故重连"触发"仍自持。

- 日期: 2026-09-20 | 状态: 已实施
- 来源: ADR-006（P1-3 确定性重连）；并发/架构审查"策略正确、机制重复造了四遍"；MQTTnet 5 移除 ManagedClient

## Context

ADR-006 P1-3 确立了 MQTT 的确定性自动重连：`ConnectAsync` 失败启动重连循环（单实例互斥），`MqttHostedService` 监督循环兜底 Faulted。但重试次数、指数退避与封顶是**自写算法**（`ComputeBackoffDelayMs` + `while (_reconnectCount < Max)` 手写循环）。

问题：

- 自写退避存在缺陷类：`base * 2^(attempt-1)` 在 attempt≈23 时 int 溢出为负，负延迟传入 `Task.Delay` 抛 `ArgumentOutOfRangeException`，重连永久停摆（由属性测试 `BackoffPropertyTests` 发现）。
- 机制重复：仓库已在协议驱动（`ReliableProtocolDriver`）与 HTTP（`HttpClientWrapper`）使用 `Polly.Core` 承载重试/退避，唯独 MQTT 自造一份，属"机制无单一归属 → 复制 + 漂移"。

外包选型的前提事实：

- MQTTnet 5（本项目 5.1.0.1559）**已移除 ManagedClient**——其 XML 无任何 Reconnect/Managed 类型。
- 官方 `MQTTnet.Extensions.ManagedClient` 在 NuGet **最新仅 4.3.7.1207，无 5.x**。
- 即：在 MQTTnet 5 上"把自动重连整体外包"不可行，除非降级到 4.x。

## Decision

- **D1 策略外包**：MQTT 重连的重试/退避改用 `Polly.Core` 的 `ResiliencePipeline`（`AddRetry`：`DelayBackoffType.Exponential` + `UseJitter` + `MaxDelay` 封顶），删除自写 `ComputeBackoffDelayMs` 与手写 attempt 循环。
- **D2 拆分连接原语**：`ConnectAsync` 拆为公开编排 + `ConnectCoreAsync`（单次连接，不触发重连）；Polly 管线复用 `ConnectCoreAsync`，避免经 `HandleConnectFailure` 自递归。
- **D3 语义保持**：总尝试次数 = `MaxReconnectAttempts`（初始 1 次 + 重试 Max-1 次），与 ADR-006 P1-3 一致；失败耗尽 → `Faulted`，交监督循环兜底。
- **D4 不降级**：保留 MQTTnet 5；重连"触发"（检测断开、单实例、启动循环）仍自持——这是 MQTTnet 5 的库限制，不是设计选择。
- **D5 契约不变**：连接状态机（Disabled/Connecting/Connected/Reconnecting/Faulted）、订阅重放（ADR-006 P1-2）、转发开关联动、监督循环均不变。

## Alternatives

- **A 降级 MQTTnet 4.3.7 + ManagedClient 4.3.7**：官方接管自动重连与订阅恢复，外包最彻底；但需重写 v5 API 调用（`MqttClientFactory`/`ResultCode`/`ReasonCode` 等），依赖降级，回归面大。
- **B 保留自写退避**：不引入依赖；但机制继续重复，且 int 溢出缺陷类仍由本项目独力维护、随时可能复发。

## Rationale

- 重试/退避是通用机制，应有单一归属；外包给经充分验证的库，消除"每个模块各造一份"的散弹式修改。
- `MaxDelay` 结构性封顶，从根上消除"退避溢出为负"这一缺陷类，无需再靠属性测试兜底。
- 与仓库既有 Polly 用法（协议驱动、HTTP）一致，降低认知与维护成本。
- 重连触发在 MQTTnet 5 无法外包属客观限制；D4 明确记录该边界，避免误以为"外包不彻底"是遗漏。

## Consequences

- `NitroGateway.Transport.MQTT` 新增依赖 `Polly.Core 8.7.0`；不再持有自写退避算法。
- 删除 `BackoffPropertyTests`（其目标方法已不存在）；新增：
  - 单测 `MqttReconnectPolicyTests`：总尝试次数 == `MaxReconnectAttempts`、退避延迟恒非负且 ≤ `MaxInterval`、Max=1 时无重试。
  - 集成测试 `MqttClientWrapperTests.Reconnect_ExhaustsExactlyMaxAttempts_ThenFaulted`：真实重连耗尽后进 Faulted，无多余尝试。
- 退避新增抖动（`UseJitter`），延迟不再确定性；尝试次数与状态语义仍确定。
- 后续：若 MQTTnet 发布 5.x 版 ManagedClient，可进一步把重连触发也外包，届时修订本 ADR。
