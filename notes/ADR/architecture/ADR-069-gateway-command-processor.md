# ADR-069: 网关命令处理器——云→网关→PLC 反向写值闭环（b 部分落地）

- 日期: 2026-08-27 | 状态: 已实施（网关侧落地；端到端联调待 `tools/mqtt-simulator` 回执模拟） | 关联: ADR-010（云侧写回设计，NitroCloud）、ADR-035（边缘角色）、DESIGN.md §4.3 命令契约、worklog 2026-08-27
- 一句话结论: 云侧命令下发闭环的网关侧补齐——新 `src/NitroGateway.Command` 订阅 `nitrogateway/+/+/commands`（QoS1）→ 校验解析 → `IWriteService` 写值 → 幂等回执 `commands/ack`。

## 问题
- DoD 5「反向写值从云端发起、收到网关回执」缺网关侧：云侧（NitroCloud）已可发布 `commands`，但 NitroGateway 不订阅、不写值、不回执，闭环断在网关。
- 契约约束（ADR-010 D8 / AGENTS.md）：命令契约以网关侧为准；命令载荷**不含 deviceId**（从 topic 第 3 段解析）；回执 `result`/`error` 必填；重试不换 commandId，网关按 commandId 幂等。

## 方案与契约（已落地）
- 新模块 `src/NitroGateway.Command`：`GatewayCommand` / `CommandAck`（result=Success|Failure、error 恒存在）/ `CommandAckSerializer`（camelCase）/ `CommandRequestParser`（topic 4 段 + siteId 一致性 + WritePoint + value 非空；value 解包为 CLR 原始类型）/ `CommandProcessor`（幂等 + 写值 + 回执）/ `CommandHostedService`（订阅 + 逐条处理）/ `AddNitroCommand()`。
- 订阅 topic：`nitrogateway/+/+/commands`（QoS1）；回执 topic：`nitrogateway/{site}/{device}/commands/ack`（QoS1）。
- 幂等：`commandId` 首达才写值并缓存回执（上限 1024）；QoS1 重投/云侧重试重发缓存回执、不重复写值；写值失败/异常 → Failure 回执（异常隔离，不中断消费循环）；回执发布失败仅记日志 + 指标（云侧扫描重发兜底）。
- 与既有架构衔接：复用 `IMqttClient` 单例（wrapper 重连自动重放订阅，故只订阅一次）；Site:Id 每条命令惰性解析（Program 建表后才写回配置）；指标 `nitro_command_processed_total(result)` / `nitro_command_ack_publish_failures_total`；Activity `CommandProcess`。

## 代码位置
- 新: `src/NitroGateway.Command/*`（8 文件）
- 改: `NitroGateway.Telemetry/Tracing/GatewayActivities.cs`（+CommandProcess）、`NitroGateway.Telemetry/NitroMetrics.cs`（+2 指标）、`NitroGateway.slnx`、`Webapi/Program.cs`（+AddNitroCommand）、`Webapi.csproj`、`tests/NitroGateway.UnitTests`（+4 测试文件 + 引用）

## 修复方向 / 验证
- 实现 bug：`CommandRequestParser.Unwrap` 三目 `l : GetDouble()` 公共类型是 double，整数被装箱成 double——两侧显式 object 化，整数值保持 long。
- 单测 20 用例（解析 14 / 处理器 4 / 宿主服务 2）：`dotnet build NitroGateway.slnx` 0 错误；`dotnet test` 780/780 通过。
- 剩余（跨项目前置，演示路径）：`tools/mqtt-simulator` 补命令回执模拟 + 云/网关/模拟端到端联调。
