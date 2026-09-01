# ADR-001: Forwarder 数据可靠性决策

- 日期: 2026-08-06 | 状态: 已实施

## Context

Forwarder 转发链路存在数据可靠性隐患：批次标记失败多次往返（3 次 DB 往返）、首轮空等一个周期、同步计数查库阻塞、节流全局共享的扩展性取舍未明确。

## Decision

- D1 MarkFailedAsync 合并为单条 UPDATE（重试计数 + 超限进死信）+ 单条 SELECT 判断，3 次往返降到 2 次。
- D2 ForwarderEngine 首轮立即执行再进 PeriodicTimer 循环（do-while），不再等满一个周期。
- D3 IForwardBuffer 新增 GetCountAsync（异步计数），ForwarderEngine/StatusController 改走异步；同步 Count 属性保留兼容并注释。
- D4 节流全局共享：v1 单 Broker 场景可接受，明确不修（ForwardingThrottle 类注释记录该决策）。

## Alternatives

- D1 保持逐条 UPDATE 直写（简单但往返多）；D2 保持固定周期（实现简单但首轮延迟一个周期）；D4 按 Broker/设备拆节流实例（更细但过度设计）。

## Rationale

- 减少 DB 往返与同步阻塞，转发热路径更稳；首轮数据立即上云减少延迟；D4 以单 Broker 现状为准，避免为未发生的多 Broker 场景引入复杂度。

## Consequences

- 在途标记/死信判定更快；首轮数据不再延迟一个周期；后续引入多 Broker 时需按实例拆分节流（v1 明确接受）。
