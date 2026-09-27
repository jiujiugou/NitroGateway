# 02 · 重试/退避/超时管线统一

- 类别：完全统一（差异是策略参数）
- 优先级：高
- 状态：**已完成**（2026-09-20）

## 现状（改造前，三处各自 `ResiliencePipelineBuilder`）

| 领域/模块 | 文件 | 配置 |
|---|---|---|
| 协议接入 | `src/NitroGateway.Protocol/Abstraction/ReliableProtocolDriver.cs` | `AddTimeout` + `AddRetry`（指数 500ms→2s） |
| 传输(HTTP) | `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs` | `AddRetry`（指数，仅幂等方法），`ShouldHandle` = 异常 + 5xx |
| 传输(MQTT) | `src/NitroGateway.Transport/MQTT/MqttClientWrapper.cs` | `AddRetry`（指数+抖动+MaxDelay） |

## 统一形态（已落地）

新增通用机制层项目 `src/NitroGateway.Primitives`：
- `ResiliencePolicy`：策略参数（`MaxRetryAttempts`/`RetryDelay`/`BackoffType`/`MaxDelay`/`UseJitter`/`Timeout`/`RetryLogLevel`/`OperationName`）。
- `ResiliencePipelineFactory.Build(...)`：非泛型（异常驱动）与泛型 `Build<TResult>(..., PredicateBuilder<TResult>)`（结果驱动）。

三处改为"只给策略参数、机制走同一工厂"：
- `ReliableProtocolDriver`：`ResiliencePolicy { Timeout, Exponential, RetryLogLevel=Debug }`
- `HttpClientWrapper`：`Build<HttpResponseMessage>` + `PredicateBuilder`（异常+5xx），`RetryLogLevel=Warning`
- `MqttClientWrapper.BuildReconnectPipeline`：`ResiliencePolicy { Exponential, UseJitter, MaxDelay, RetryLogLevel=Information }`

## 差异是参数还是语义

参数：`ShouldHandle`、attempts、backoff、timeout、是否仅幂等。无领域语义。

## 涉及改动文件（已完成）

- [x] 新增 `src/NitroGateway.Primitives`（`ResiliencePolicy` + `ResiliencePipelineFactory`）
- [x] `ReliableProtocolDriver.cs` 改调工厂
- [x] `HttpClientWrapper.cs` 改调工厂
- [x] `MqttClientWrapper.cs` 改调工厂
- [x] `NitroGateway.slnx` 收录新项目；三个消费方加 `ProjectReference`

## 验收 / 测试（结果）

- `ResiliencePipelineFactoryTests`（新增 6 项）：重试次数 = Max+1、Max=0 不重试、MaxDelay 封顶、超时按截止生效、泛型结果判定重试、取消不重试。
- 全量单测：**912 通过 / 0 失败**；集成测试：**60 通过 / 0 失败**。

## 备注 / 踩坑

- Polly 对 `MaxDelay` 有 **[0, 1 天]** 校验区间：未提供时不可显式设 `TimeSpan.MaxValue`（会抛 `ValidationException`），应保持默认值。
- 超时策略**先于重试加入**，语义是整条管线的截止时间（与原 `ReliableProtocolDriver` 顺序一致，未改行为）——原注释"每次尝试独立超时"表述不准，已修正。
- 建了首条机制统一 ADR：`notes/ADR/architecture/ADR-076-...`（新增共享机制项目属模块边界变更）。
