# ADR-006: Transport MQTT 优化清单

- 日期: 2026-08-07
- 状态: 全部条目已修复（2026-08-07）
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: src/NitroGateway.Transport/MQTT 全模块（MqttClientWrapper / MqttHostedService / MqttServiceCollectionExtensions / IMqttClient）+ Webapi 接线（MqttHealthCheck / DeviceStatusDispatcher）

## 已修复（2026-08-07）

- P1-1 ClientId 唯一：只截 GUID 后缀 8 位（`NitroGateway-{MachineName}-{guidSuffix}`），修复前整串 [..8] 恒为 "NitroGat"
- P1-2 重连重放订阅：wrapper 记录已订阅主题（topic→qos），ConnectAsync 成功（含重连）后 ReplaySubscriptionsAsync 重放
- P1-3 重连无恢复路径：ConnectAsync 失败确定性启动重连循环（HandleConnectFailure，不再依赖 DisconnectedAsync 事件时序）；循环单实例互斥（StartReconnectLoop）；MqttHostedService 改监督循环（Faulted 或 Disconnected+已配置自动重连时按 ReconnectMaxIntervalMs 周期兜底）
- P2-1 cmd 订阅移除：全仓无消费方且云端指令走 HTTP（Transport/DESIGN.md），移除 `nitrogateway/+/cmd` 订阅；Messages 通道保留给未来消费者
- P3-1 通道满丢弃：因无下行订阅不再有命令消息流入，通道保留并在 OnMessageReceivedAsync 注释明确「未来命令下行应改用 WriteAsync 阻塞写入」；当前 TryWrite+警告仅作遥测兜底
- P3-2 TryReconnectAsync 退出（成功/失败/取消）即释放 `_reconnectCts`（finally）
- P3-3 ExecuteAsync 常驻监督（BackgroundService 生命周期内循环），不再一次性返回；无订阅调用故结果忽略问题消除
- P3-4 DisposeAsync 先置 Disconnected 再拆线，关停期间健康检查立即转不健康
- P3-5 Host/Port 改 init 与其余属性一致（ConfigurationBinder 兼容已验证）；KeepAliveSeconds 夹紧 [5, 3600]

## 测试

- 新增 MqttClientWrapperTests 8 个 + MqttHostedServiceTests 3 个（NitroGateway.IntegrationTests，基于注入的 FakeMqttInnerClient 替身，无需真实 broker）：ClientId 唯一、KeepAlive 夹紧、Host/Port 不可变、重连重放订阅、首连失败自动重连恢复、耗尽进 Faulted 后可恢复、Dispose 置 Disconnected、重连成功路径 CTS 释放、监督循环 Faulted 恢复 / Disconnected 周期重试 / 无订阅调用
