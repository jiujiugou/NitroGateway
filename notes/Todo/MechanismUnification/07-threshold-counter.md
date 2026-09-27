# 07 · 阈值化连续计数原语（只统一原语）

- 类别：原语统一（**不合并状态机**）
- 优先级：中
- 状态：待办

## 现状（三处各写"连续计数 → 阈值触发"）

| 领域 | 文件 | 计数/阈值 |
|---|---|---|
| 设备健康 | `src/NitroGateway.Device/DeviceHealthMonitor.cs:44-102` | 连续失败 3 → Offline；连续成功 3 → Online |
| 传输(HTTP) | `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs:179-221` | 连续失败 ≥ `MaxRetries+1` → Faulted |
| 采集(熔断) | `src/NitroGateway.Collection/Resilience/CircuitBreaker.cs` | 探测失败/成功驱动状态迁移（含冷却翻倍） |

## 只统一这个原语

`FailureCounter` / `ThresholdGate`：
- 机制：线程安全地累加/复位；达到阈值时发事件；返回是否越阈。
- **不动**各领域的状态集、迁移语义、冷却策略。

## 为什么不能合并状态机

`CircuitBreaker.cs:7` 明确："只保护执行，不判定故障"；HealthMonitor 是"唯一健康决策者"。三者语义不同，合并会造出"万能状态机"。

## 差异是参数还是语义

原语内的阈值是参数；**状态集与迁移是各自的语义**，保留在领域内。

## 涉及改动文件（待办）

- [ ] 新增 `FailureCounter`（通用机制层）
- [ ] `DeviceHealthMonitor.cs`、`HttpClientWrapper.cs` 改用（可选：熔断的计数部分）
- [ ] 不触碰各状态机迁移逻辑

## 验收 / 测试

- 上述模块现有单测保持绿。
- 新增 `FailureCounter` 并发单测：并发累加不丢计数、阈值恰触发一次、复位。

## 备注

收益中等，风险在于"抽错粒度"；先只抽计数，不抽状态机。
