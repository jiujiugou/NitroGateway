# ADR-006: Transport MQTT 优化决策

- 日期: 2026-08-07 | 状态: 已实施

## Context

MQTT 传输层存在 ClientId 重复、重连不重放订阅、重连无恢复路径、无用 cmd 订阅、通道/重连/生命周期细节缺陷。

## Decision

- D1 ClientId 唯一：只取 GUID 后缀 8 位（NitroGateway-{MachineName}-{guidSuffix}），修复前整串截断恒为 "NitroGat"。
- D2 重连重放订阅：wrapper 记录已订阅主题（topic→qos），ConnectAsync 成功（含重连）后重放订阅。
- D3 确定性重连：ConnectAsync 失败启动重连循环（单实例互斥，不再依赖 DisconnectedAsync 事件时序）；MqttHostedService 改监督循环，Faulted/Disconnected+已配置自动重连时按周期兜底。
- D4 移除 nitrogateway/+/cmd 订阅：全仓无消费方且云端指令走 HTTP（Transport/DESIGN.md）；Messages 通道保留给未来消费者，未来命令下行应改用 WriteAsync 阻塞写入。
- D5 细节契约：TryReconnectAsync 退出（成功/失败/取消）即释放 _reconnectCts；ExecuteAsync 常驻监督（不一次性返回）；DisposeAsync 先置 Disconnected 再拆线；Host/Port 改 init；KeepAliveSeconds 夹紧 [5, 3600]。

## Alternatives

- D3 备选：仅依赖 DisconnectedAsync 事件重连（实现简单，但事件时序不可靠导致无恢复路径）。
- D4 备选：保留 cmd 订阅（未来可能用，但当下无消费者且有安全面）。

## Rationale

- 唯一 ClientId 避免会话冲突；订阅重放保证重连后契约不丢；确定性重连循环+监督兜底保证自愈；移除无用订阅收窄攻击面。

## Consequences

- 断连/重连后自动恢复订阅与连接；无下行命令消费时不再订阅 cmd topic；关停期间健康检查立即转不健康；未来命令下行需按注释约定走阻塞写入。
