# ADR-020: Transport 模块二轮 Code Review 决策

- 日期: 2026-08-09 | 状态: 已实施（P1/P2/P3 全部闭环）；HTTP 子模块本轮首次覆盖

## Context

Transport 二轮 review 发现：IMqttStateListener 死接线、ConnectAsync 取消语义不清、HTTP 异常分类/幂等重试缺失、监督循环日志刷屏、Options 无校验、重连异常未兜底、并发竞态、QoS1 无订阅者误判失败。首轮 ADR-006 已修条目不重复。

## Decision

- D1 IMqttStateListener 接线：MqttClientWrapper 注入 IEnumerable<IMqttStateListener>，SetState 经 NotifyStateListeners 通知（fire-and-forget + 异常隔离，监听者故障只记日志）。
- D2 ConnectAsync 取消语义：catch 前置 catch (OperationCanceledException)——取消不是失败，回落到 Disconnected 后 rethrow，不触发重连；TryReconnectAsync 内捕 OCE 正常退出；MqttHostedService 监督循环捕 OCE break（停机干净）。
- D3 HTTP 异常分类：ClassifyError 按类型分类（TaskCanceledException→Timeout；HttpRequestException→CommunicationError；其余 General）；调用方取消（ct.IsCancellationRequested）上抛 OCE 不计连续失败。
- D4 HTTP 幂等重试：仅幂等方法（GET/PUT/DELETE/HEAD/OPTIONS/TRACE）走重试管线；POST（UploadAsync）走直通不重试（ADR-011 落地时以 batchId 幂等键重新开启）；每次尝试新建 HttpRequestMessage（修复"request message was already sent"）。
- D5 监督循环日志限流：重连失败 LogWarning→LogDebug、Faulted LogError→LogWarning（broker 长期不可用不再刷屏）。
- D6 Options 校验/夹紧：MqttConnectionOptions 加 SectionName + Host/Port/数值 clamp；MqttServiceCollectionExtensions 走 Options+Validate+ValidateOnStart（缺 MQTT 段不再 NRE）；HttpConnectionOptions 数值 clamp。
- D7 TryReconnectAsync 异常兜底：外层 catch-all 置 Faulted 交监督循环（不再未观测异常卡 Reconnecting）。
- D8 并发安全：MqttClientWrapper _stateLock+State 锁读、HttpClientWrapper _stateLock+Interlocked（Singleton 并发发布无竞态）。
- D9 PublishAsync NoMatchingSubscribers 按成功：注释明确 QoS1 尽力投递决策（无订阅者不计失败）。

## Alternatives

- D2 备选：把取消当失败触发重连（会导致停机/取消时反复重连）。

## Rationale

- 取消不是故障，语义分离保证停机干净；HTTP 幂等重试避免重复副作用；状态机与日志收敛；配置校验启动即暴露；并发安全支持多路复用。

## Consequences

- 状态监听生效（SignalR/UI 联动）；取消不触发无谓重连；HTTP 幂等可靠、POST 不重试；长期断连不再刷日志；并发发布无竞态。
