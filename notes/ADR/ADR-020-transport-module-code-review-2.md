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

## 待处理条目（P3）

- P3-1 监督循环/状态日志刷屏：MqttHostedService 监督重连失败每周期 LogWarning（MqttHostedService.cs:39-40）+ OnStateChanged Faulted LogError（:73），broker 长期不可用时每 30s 一组，与 ADR-016 P2-1 热路径日志降级目标相悖。修复方向：失败明细降 Debug 或按次数限流。
- P3-2 Options 无校验/无夹紧：MqttConnectionOptions.Port 无默认、Host 空串不校验；MqttServiceCollectionExtensions 对 Get 结果为 null 无防护（配置缺 MQTT 段 → 启动 NRE）；ReconnectBackoffBaseMs<=0 时 TryReconnectAsync 的 Task.Delay 抛 ArgumentOutOfRangeException；HttpConnectionOptions.TimeoutMs<=0 / MaxRetries<0 同理。修复方向：注册时校验/夹紧（对齐 ADR-016 P2-2 CollectionOption 模式）。
- P3-3 TryReconnectAsync fire-and-forget 无异常兜底：`_ = TryReconnectAsync()`（MqttClientWrapper.cs:378）内部无 catch-all，非 OCE 异常（Task.Delay 参数非法等）→ 未观测异常且状态卡 Reconnecting。修复方向：方法内 catch-all 记 Error 并置 Faulted/Disconnected。
- P3-4 DESIGN.md 漂移 ×3：`MqttConnectionOptions.Broker` vs 实际 Host+Port；IHttpClient 仅列 SendAsync vs 实际 UploadAsync/HealthCheckAsync；约束 6「配置数据同步用 QoS=2」全仓无 QoS2 发布点（Forwarder / MqttAlarmNotifier 均 QoS1）。修复方向：DESIGN.md 同步实际签名，QoS2 约束删除或标注预留。
- P3-5 并发安全：MqttClientWrapper.State/SetState、HttpClientWrapper._consecutiveFailures/State 无同步，Singleton 实例被 Forwarder + MqttAlarmNotifier 并发发布时状态与计数竞态（当前 Forwarder 串行，影响面小）。修复方向：状态读写加锁或 Interlocked。
- P3-6 PublishAsync 把 NoMatchingSubscribers 当成功：无订阅者时 QoS1 消息被 Broker 丢弃仍返回 Success（MqttClientWrapper.cs:175），与"至少一次"语义矛盾；遥测场景可接受但需注释明确决策。修复方向：注释说明（QoS1 为尽力投递，无订阅者不计失败）。
- P3-7 小项：MqttMessage.ReceivedAt 用 DateTime 未标注 UTC；绕过 AddNitroMqtt 直接构造且 ClientId 为空时每次 ConnectAsync 重新生成 ID（会话漂移）；HttpConnectionOptions.HealthPath 注释「留空不做主动健康检查」与实现默认 /health（HttpClientWrapper.cs:121）不符。修复方向：注释/文档对齐。

## 亮点

- ADR-006 修复扎实且有测试锁定：重连单实例互斥、订阅重放、Faulted 监督兜底、CTS 释放（MqttClientWrapperTests 8 + MqttHostedServiceTests 3）
- PublishAsync 追踪 Activity + 错误标签齐全；MQTT/HTTP 接口统一 OperationResult、不抛异常契约一致
- HTTP 状态机简洁（连续失败 MaxRetries+1 次进 Faulted），Polly 重试配置集中
