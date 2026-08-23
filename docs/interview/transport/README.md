# Transport 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Transport`（传输层：封装 MQTT / HTTP 两种通信通道，供数据转发与告警通知使用）。题目全部基于当前代码真实实现编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来、讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + `notes/ADR/forwarder/ADR-006-transport-mqtt-optimization.md` + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（实现细节/失败路径/并发）· ★★★ 深水（设计权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
模块定位（README / DESIGN.md）
→ IMqttClient 接口（抽象约定：OperationResult / IAsyncEnumerable）
→ MqttClientWrapper（连接生命周期 / 指数退避重连 / 重放订阅 / 消息管道）
→ MqttHostedService（监视循环与兜底重连）
→ MqttConnectionOptions + AddNitroMqtt（配置、ClientId、DI 生命周期）
→ IHttpClient + HttpClientWrapper（HTTP 三态状态机 / Polly 重试）
→ 消费方视角（Forwarder / MqttAlarmNotifier / MqttHealthCheck / StatusController / DeviceStatusDispatcher）
→ ADR-006 复盘（每个 P 条目对应哪段代码）
→ 测试收尾（MqttClientWrapperTests / MqttHostedServiceTests / FakeMqttInnerClient）
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 设计文档 | `src/NitroGateway.Transport/DESIGN.md`、`README.md` | 定位：只管「发出去/收进来」，不管消息内容与序列化 |
| MQTT 接口 | `src/NitroGateway.Transport/MQTT/IMqttClient.cs` | 连接/发布/订阅统一异步接口，全部返回 OperationResult |
| MQTT 状态机 | `src/NitroGateway.Transport/MQTT/MqttConnectionState.cs` | Disconnected/Connecting/Connected/Reconnecting/Faulted |
| MQTT 参数 | `src/NitroGateway.Transport/MQTT/MqttConnectionOptions.cs` | init 不可变、KeepAlive 夹紧 [5,3600]、重连三项参数 |
| MQTT 实现 | `src/NitroGateway.Transport/MQTT/MqttClientWrapper.cs` | MQTTnet 封装：生命周期、单实例重连循环、重放订阅、Channel 消息管道、Activity 追踪 |
| 监视服务 | `src/NitroGateway.Transport/MQTT/MqttHostedService.cs` | Faulted / Disconnected 兜底重连的 BackgroundService |
| DI 注册 | `src/NitroGateway.Transport/MQTT/MqttServiceCollectionExtensions.cs` | 读 "MQTT" 配置节、自动唯一 ClientId、Singleton + HostedService |
| 状态监听接口 | `src/NitroGateway.Transport/MQTT/IMqttStateListener.cs` | 供上层异步监听状态（注意：当前定义了但无人调用） |
| HTTP 接口 | `src/NitroGateway.Transport/HTTP/IHttpClient.cs` | Send / Upload / HealthCheck，统一 OperationResult |
| HTTP 实现 | `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs` | HttpClient + Polly 重试 + 三态状态机（注释与实现有偏差） |
| HTTP 参数/载体 | `HTTP/HttpConnectionOptions.cs`、`HttpRequest.cs`、`HttpResponse.cs`、`HttpAuthType.cs` | 数据载体不依赖 ASP.NET Core |
| HTTP DI | `src/NitroGateway.Transport/HTTP/HttpServiceCollectionExtensions.cs` | 注册 Singleton，无 HostedService |

## 跨模块依赖（答题时需要知道的上下文）

- `IMqttClient` 消费方：`Forwarder`（QoS1 转发核心链路，Publish 成功才 Commit）、`MqttAlarmNotifier`（告警推送，topic `nitrogateway/{deviceId}/alarms`）、`MqttHealthCheck`（健康检查三档映射）、`StatusController`（暴露 MqttState / MqttConnected）、`DeviceStatusDispatcher`（实现 IMqttStateListener，SignalR 出口）。
- `ForwarderEngine` 每轮从 DI 解析 `IMqttClient`，`State != Connected` 时跳过本轮排空（`ForwarderEngine.cs:118-121`）。
- `notes/ADR/forwarder/ADR-006-transport-mqtt-optimization.md` 是 MQTT 优化清单：P1-1 ClientId 唯一、P1-2 重连重放订阅、P1-3 重连无恢复路径、P2-1 移除 cmd 订阅、P3-1~P3-5 管道丢弃/CTS 释放/监视循环/Dispose 状态/参数边界，全部已修复（2026-08-07）。

## 注意事项

- 代码是唯一事实来源：`IMqttClient` 接口注释写「基于 IHttpClientFactory + Polly」，实现却是直接 `new HttpClient`；`IMqttStateListener` 定义且被实现，但 `OnStateChangedAsync` 全仓库无人调用——这类「文档/接口与实现偏差」正是深水考点。
- 测试是理解行为最快的捷径：`tests/NitroGateway.IntegrationTests/MqttClientWrapperTests.cs`（8 个）+ `MqttHostedServiceTests.cs`（3 个），基于注入的 `FakeMqttInnerClient` 替身，无需真实 broker；HTTP 侧目前没有专门测试。
- 消息管道当前无消费者（P2-1 移除 cmd 订阅后保留给未来），答题时注意区分「现状」与「设计意图」。
- 答完全部题目后，试着不看代码口述「broker 断线 5 分钟 → 恢复 → 恢复转发」的完整时序，能讲通才算吃透。
