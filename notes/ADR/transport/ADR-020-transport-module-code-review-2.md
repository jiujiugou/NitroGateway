# ADR-020: Transport 模块二轮 Code Review 清单

- 日期: 2026-08-09 | 状态: P1×2 + P2×3 已修复（2026-08-09），P3×7 待处理
- 用途: Transport 模块二轮 review 问题清单；修复后在代码加注释并删除本条
- 范围: src/NitroGateway.Transport（MQTT + HTTP 两子模块）+ Webapi 接线（SignalRServiceCollectionExtensions / DeviceStatusDispatcher / MqttHealthCheck）+ 消费方（Forwarder / MqttAlarmNotifier）+ 相关测试
- 首轮 ADR-006 已修条目不重复；首轮范围仅 MQTT，HTTP 子模块本轮首次覆盖

## 修复记录（2026-08-09）

- P1-1 IMqttStateListener 死接线：`MqttClientWrapper` 注入 `IEnumerable<IMqttStateListener>`，`SetState` 经 `NotifyStateListeners` 通知（fire-and-forget + 异常隔离，监听者故障只记日志）；`IMqttStateListener` 补 XML 文档；Webapi 原有注册（SignalRServiceCollectionExtensions.cs:25 → DeviceStatusDispatcher → 前端 App.vue `MqttStateChanged`）即生效，无需改注册。红绿：`StateChange_NotifiesMqttStateListeners` 先红后绿
- P1-2 ConnectAsync 取消语义：catch 前置 `catch (OperationCanceledException)`——取消不是失败，回落到 Disconnected 后 rethrow，不触发重连；`TryReconnectAsync` 内 ConnectAsync 捕 OCE 正常退出；`MqttHostedService` 监督循环捕 OCE break（停机干净）。红绿：`ConnectAsync_Cancelled_DoesNotStartReconnectLoop` 先红后绿（`FakeMqttInnerClient.ConnectAsync` 补取消令牌支持）
- P2-1 HTTP 异常分类：`ClassifyError` 按类型分类（TaskCanceledException→Timeout；HttpRequestException→CommunicationError；其余 General）；调用方取消（`ct.IsCancellationRequested`）上抛 OCE 不计连续失败。红绿：`Timeout_ClassifiedAsTimeout` / `HttpRequestException_ClassifiedAsCommunication` / `CallerCancellation_IsNotCountedAsFailure`
- P2-2 HTTP 幂等重试：仅幂等方法（GET/PUT/DELETE/HEAD/OPTIONS/TRACE）走重试管线，POST（`UploadAsync`）走直通管线不重试（ADR-011 落地时以 batchId 幂等键重新开启）；顺带修复潜伏 bug——重试复用同一 `HttpRequestMessage` 抛 "request message was already sent"（改为每次尝试新建）。红绿：`IdempotentGet_RetriesOnServerError` / `UploadPost_DoesNotRetry`
- P2-3 HTTP 测试补齐：新增 `HttpClientWrapperTests` 7 个（注入 `HttpMessageHandler` 替身，覆盖状态迁移/异常分类/幂等重试）；HTTP csproj 清理未使用依赖（Microsoft.Extensions.Http / Http.Resilience 从未使用，改显式 `Polly.Core 8.7.0` 与 Protocol.Abstractions 一致，消除 NU1605）
- 验证: build 0 错误；UnitTests 215；IntegrationTests 40（31 + 新增 9）

## 修复记录（2026-08-10，P3 全部修复，条目已清）

- P3-1 监督循环日志限流：MqttHostedService 重连失败 LogWarning→LogDebug、Faulted LogError→LogWarning（broker 长期不可用不再每 30s 刷屏）
- P3-2 Options 校验/夹紧：MqttConnectionOptions 加 SectionName 常量 + Host/Port/数值 clamp；MqttServiceCollectionExtensions 走 Options+Validate+ValidateOnStart（配置缺 MQTT 段不再 NRE）；HttpConnectionOptions 数值 clamp
- P3-3 TryReconnectAsync 异常兜底：外层 catch-all 置 Faulted 交监督循环（不再未观测异常卡 Reconnecting）
- P3-4 Transport/DESIGN.md 同步实际签名（Host+Port / UploadAsync / HealthCheckAsync），QoS2 约束改「预留」
- P3-5 并发安全：MqttClientWrapper `_stateLock`+State 锁读、HttpClientWrapper `_stateLock`+Interlocked（Singleton 并发发布无竞态）
- P3-6 PublishAsync NoMatchingSubscribers 按成功：注释明确 QoS1 尽力投递决策（无订阅者不计失败）
- P3-7 小项：MqttMessage.ReceivedAt 注释明确 UTC；HttpConnectionOptions.HealthPath 注释修正；MqttClientWrapper `_clientId` 构造时固定（防会话漂移）
## 亮点

- ADR-006 修复扎实且有测试锁定：重连单实例互斥、订阅重放、Faulted 监督兜底、CTS 释放（MqttClientWrapperTests 8 + MqttHostedServiceTests 3）
- PublishAsync 追踪 Activity + 错误标签齐全；MQTT/HTTP 接口统一 OperationResult、不抛异常契约一致
- HTTP 状态机简洁（连续失败 MaxRetries+1 次进 Faulted），Polly 重试配置集中
