# ADR-076: 通用机制层 NitroGateway.Primitives（机制与策略分离）

> **摘要**: 新增通用机制层 `NitroGateway.Primitives`，把跨领域复用的"机制"收敛成一份（首个：重试/退避/超时管线工厂）；领域只保留"策略参数"。

- 日期: 2026-09-20 | 状态: 已实施
- 来源: 架构审查"策略正确，机制重复造了四遍"；待办清单 `notes/Todo/MechanismUnification/`

## Context

系统有 7 个领域（协议接入/采集/转发/命令回写/设备管理/告警/设备健康）+ 通用域。**通用机制没有单一归属**，于是每个领域在自己的类内部各造一份：

- 重试/退避：协议驱动（`ReliableProtocolDriver`）、HTTP（`HttpClientWrapper`）、MQTT（`MqttClientWrapper`）三处各自手配 Polly。
- 连接/失败状态机、错误分类、TTL 缓存、幂等存储、有界队列等亦多处重复。

根因：**策略与机制耦合**——机制写在领域类内部，无法复用，只能复制；复制即漂移（如 MQTT 自写退避的 int 溢出缺陷）。

判据：

> 差异能用**参数/策略**表达 → 统一；差异是**领域语义** → 不统一。

首个收敛项为"重试/退避/超时管线"（差异全是参数）。

## Decision

- **D1 新增通用机制层** `src/NitroGateway.Primitives`：依赖 `Shared` 与第三方库，**不依赖任何领域模块**；后续机制（错误分类、TTL 缓存、幂等存储、有界消费者、监听器扇出）统一落此。
- **D2 首个机制**：`ResiliencePolicy`（策略参数）+ `ResiliencePipelineFactory`（机制：非泛型异常驱动 / 泛型结果驱动两种 `ResiliencePipeline`）。
- **D3 三处消费方改调工厂**：`ReliableProtocolDriver`、`HttpClientWrapper`、`MqttClientWrapper.BuildReconnectPipeline` 只提供策略参数；行为不变（尝试次数语义、`ShouldHandle`、超时顺序、日志级别均以参数保留）。
- **D4 不统一状态机**：熔断（门控）/设备健康（判定）/连接生命周期语义不同，只可抽底层原语，不合并（见待办 07–09）。
- **D5 未并入 Shared**：Shared 是共享内核，`Domain → Shared`；若把 Polly 放入 Shared 会传染给 Domain。故独立成层。

## Alternatives

- **A 放入 `NitroGateway.Shared`**：省一个项目；但 Shared 会被 Domain 引用，导致 Domain 传递依赖 Polly，违反分层。
- **B 各处继续手配 Polly**：零结构变更；但机制继续复制、漂移（散弹式修改）。
- **C 合并 3 个领域**：把协议/HTTP/MQTT 合成一个"传输域"；但三者领域语义不同，合并会造出耦合巨物。

## Rationale

- 机制单一归属，消除散弹式修改；新增领域复用同一机制而非再复制。
- 优先"结构强制"：领域只能通过 `ResiliencePipelineFactory` 构造管线，想绕都绕不过。
- 与仓库既有 Polly 用法一致（协议/HTTP 已用 Polly），迁移成本低。

## Consequences

- `NitroGateway.Primitives` 入 `NitroGateway.slnx`；`Protocol.Abstractions`、`Transport.HTTP`、`Transport.MQTT` 增加 `ProjectReference`。
- 新增 `ResiliencePipelineFactoryTests`（策略契约：次数、封顶、超时、取消）；单测 912 / 集成 60 全绿。
- 约定：**`ResiliencePolicy.MaxDelay` 不得设 `TimeSpan.MaxValue`**——Polly 校验区间为 [0, 1 天]，未提供时应保持默认。
- 约定：超时策略**先于重试加入**，语义为整条管线截止时间；若要"每次尝试独立超时"需改变加入顺序，属独立决策。
- 后续机制按 `notes/Todo/MechanismUnification/README.md` 推进；其中 **03 错误分类**经评估在"仅 2 处、协议驱动未迁"时 ROI 过低，**已撤回**，待真正统一协议驱动错误时再做（届时该类型因只依赖 `OperationalError` 应放 `Shared`，而非本层）。
