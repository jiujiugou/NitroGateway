# 09 · 监听器扇出原语（只统一原语）

- 类别：原语统一
- 优先级：低
- 状态：待办

## 现状

| 位置 | 文件 | 做法 |
|---|---|---|
| 设备健康 | `src/NitroGateway.Device/DeviceHealthMonitor.cs:136-147` | `ConcurrentBag<IDeviceHealthListener>` + fire-and-forget `ObserveListenerAsync`（异常只记日志） |
| MQTT 状态 | `src/NitroGateway.Transport/MQTT/MqttClientWrapper.cs` `NotifyStateListeners` | fire-and-forget + 异常隔离 |
| 采集数据 | `IPointStoredSink`（`src/NitroGateway.Domain/Events/IPointStoredSink.cs`） | 多 sink 分发 |

"注册监听者 → 扇出 → 逐个异常隔离、不阻塞发布方"被重复实现。

## 只统一这个原语

`ListenerSet<TEvent>`：注册/退订、扇出、**每个监听者异常隔离**、fire-and-forget 显式观察。
保留各自的事件载荷类型与触发点。

## 涉及改动文件（待办）

- [ ] 新增 `ListenerSet<TEvent>`（通用机制层）
- [ ] `DeviceHealthMonitor.cs`、`MqttClientWrapper.cs` 改用
- [ ] （评估）`IPointStoredSink` 是否纳入

## 验收 / 测试

- 现有监听器相关单测保持绿。
- 新增：某监听者抛异常不影响其它监听者与发布流程。

## 备注

优先级低；有**过度抽象**风险（事件载荷各异）。先只统一"异常隔离 + fire-and-forget 观察"这一点，不要强行统一泛型分发。
