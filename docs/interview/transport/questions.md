# Transport 模块面试题（questions）

共 8 组 44 题。先不看书作答，再对照代码定位核对，最后用 `answers.md` 自检。

## 一、模块定位与抽象设计

**Q1.1 ★** 为什么 Transport 要拆成 MQTT 和 HTTP 两个独立子项目？模块的职责边界是什么？它「不关心」什么？
代码定位：`src/NitroGateway.Transport/DESIGN.md`、`README.md`。

**Q1.2 ★** `IMqttClient` 为什么所有操作都返回 `OperationResult` 而不是抛异常？这给调用方（Forwarder）带来什么约定？调用方还需要处理异常吗？
代码定位：`src/NitroGateway.Transport/MQTT/IMqttClient.cs:9`（接口注释）；`MqttClientWrapper.cs:73-117`（ConnectAsync 内部 try-catch 转结果）。

**Q1.3 ★★** `Messages` 为什么用 `IAsyncEnumerable` 而不是事件回调？`Channel` 在这里的角色是什么？背压体现在哪里？
代码定位：`IMqttClient.cs:37-38`；`MqttClientWrapper.cs:42`（ReadAllAsync）、`MqttClientWrapper.cs:276-305`（OnMessageReceivedAsync）。

**Q1.4 ★** `HttpRequest` / `HttpResponse` 为什么刻意不依赖 ASP.NET Core 类型？`HttpResponse.IsSuccessStatusCode` 的判定规则？
代码定位：`src/NitroGateway.Transport/HTTP/HttpRequest.cs`、`HTTP/HttpResponse.cs`。

**Q1.5 ★★** `IMqttStateListener` 定义在 Transport，实现却在 Webapi 的 `DeviceStatusDispatcher`，但全仓库无人调用 `OnStateChangedAsync`——它和 `StateChanged` 事件是什么关系？「定义了未接线」说明什么？
代码定位：`MQTT/IMqttStateListener.cs:8`；`src/NitroGateway.Webapi/Hubs/DeviceStatusDispatcher.cs:14,66`；`src/NitroGateway.Webapi/Hubs/SignalRServiceCollectionExtensions.cs:25`。

## 二、MQTT 连接状态机与重连（MqttClientWrapper）

**Q2.1 ★** 完整画一遍状态迁移图：Disconnected / Connecting / Connected / Reconnecting / Faulted 每个转移分别由哪段代码在什么时机触发？（含首连失败、意外断开、主动断开、Dispose 四条路径）
代码定位：`MqttConnectionState.cs`；`MqttClientWrapper.cs:73-117`（ConnectAsync）、`:119-142`（DisconnectAsync）、`:234-258`（DisposeAsync）、`:260-274`（SetState）、`:307-328`（OnDisconnectedAsync）、`:330-369`（TryReconnectAsync）、`:383-393`（HandleConnectFailure）。

**Q2.2 ★** 指数退避公式和两个上限参数分别是什么？`MaxReconnectAttempts = 0` 的语义是什么？代码里哪些分支为它专门做了判断？
代码定位：`MqttConnectionOptions.cs:37-47`；`MqttClientWrapper.cs:338-344`（退避计算）、`:312-318`、`:387-390`（=0 分支）；`MqttHostedService.cs:36-38`。

**Q2.3 ★★** `_reconnectLock` + `_reconnectLoopActive` 保护什么？有哪些入口可能并发触发重连循环？如果不做单实例互斥会发生什么？
代码定位：`MqttClientWrapper.cs:27-29`、`:371-381`（StartReconnectLoop）、`:355-368`（finally 复位）。

**Q2.4 ★★** 为什么首连失败也要「确定性」启动重连（`HandleConnectFailure`），而不是依赖 `DisconnectedAsync` 事件？历史教训是什么（ADR-006 P1-3）？
代码定位：`MqttClientWrapper.cs:111-115,383-393`；`notes/ADR/forwarder/ADR-006-transport-mqtt-optimization.md` P1-3。

**Q2.5 ★★** `OnDisconnectedAsync` 里两个条件——`MaxReconnectAttempts == 0` 直接置 Disconnected、只有 `State == Connected` 才启动重连——分别防什么？为什么不处理 Reconnecting / Faulted / Disconnected 状态？
代码定位：`MqttClientWrapper.cs:307-328`。

**Q2.6 ★★** `WithCleanStart()` 会带来什么副作用？`ReplaySubscriptionsAsync` 如何补救？订阅记录何时写入、何时清除？（提示：找缺陷）
代码定位：`MqttClientWrapper.cs:81-88`（CleanStart）、`:203-215`（SubscribeAsync 记录）、`:395-407`（ReplaySubscriptionsAsync）。

**Q2.7 ★★** `TryReconnectAsync` 的 finally 做了什么？为什么成功 / 耗尽 / 取消三条退出路径都必须释放 CTS 并复位循环标记？不释放会有什么后果？
代码定位：`MqttClientWrapper.cs:355-368`；`:409-414`（CancelReconnect）。

**Q2.8 ★★** `ConnectAsync` 成功后 `_reconnectCount = 0` 的作用？如果不重置，broker 反复抖动时会怎样？
代码定位：`MqttClientWrapper.cs:88-89`、`:338-344`。

**Q2.9 ★★** `PublishAsync` / `SubscribeAsync` 在 `State != Connected` 时返回什么错误？为什么「快速失败」而不是阻塞等待重连？这和 `ForwarderEngine` 的「跳过本轮」如何配合？
代码定位：`MqttClientWrapper.cs:144-192`、`:194-232`；`src/NitroGateway.Forwarder/ForwarderEngine.cs:118-121`。

**Q2.10 ★★** `DisconnectAsync` 为什么必须先 `CancelReconnect()` 再断开？顺序反了会怎样？
代码定位：`MqttClientWrapper.cs:119-123`。

## 三、消息管道与 QoS

**Q3.1 ★★** 消息 Channel 的容量与 FullMode 是什么？为什么 FullMode 是 Wait 却用 `TryWrite`？写满时发生什么？
代码定位：`MqttClientWrapper.cs:50-56`（创建 Channel）、`:298-303`（TryWrite + 警告）。

**Q3.2 ★★** 代码注释说未来命令下行应改用 `WriteAsync` 阻塞写或独立小容量队列——当前「TryWrite 丢弃 + 警告」的风险是什么？为什么能接受？
代码定位：`MqttClientWrapper.cs:278-283`（OnMessageReceivedAsync 注释，ADR-006 P3-1）。

**Q3.3 ★★** `payload` 是 `ReadOnlySequence<byte>`，代码如何拷贝成 `byte[]`？为什么不能直接持有原始 buffer 的引用？
代码定位：`MqttClientWrapper.cs:284-291`。

**Q3.4 ★★** `PublishAsync` 判定成功的两种 ReasonCode 是什么？`NoMatchingSubscribers` 为什么算成功？在业务上（Forwarder Commit）有什么坑？
代码定位：`MqttClientWrapper.cs:165-173`；`src/NitroGateway.Forwarder/Forwarder.cs`（Publish 成功才 Commit）。

**Q3.5 ★★★** QoS1 at-least-once 由哪两层机制叠加保证？列举所有会产生「重复投递」的崩溃窗口；会丢数据吗？云侧应如何配合？
代码定位：`Forwarder.cs`（Dequeue→Serialize→Publish→Commit 顺序）；`IMqttClient.cs` QoS 注释。

## 四、MqttHostedService 监视循环

**Q4.1 ★★** wrapper 内部已经有自动重连，为什么还需要 `MqttHostedService`？两者分工是什么？什么场景下 wrapper 会「放弃」而由监视循环接管？
代码定位：`MqttHostedService.cs:25-57`；`MqttClientWrapper.cs:330-369`。

**Q4.2 ★★** 监视循环在什么状态下会主动 `ConnectAsync`？为什么 `Disconnected` 分支要附加 `MaxReconnectAttempts > 0` 条件？
代码定位：`MqttHostedService.cs:33-40`。

**Q4.3 ★★** 循环周期为什么恰好用 `ReconnectMaxIntervalMs`？它与 wrapper 内部指数退避是什么关系？两条重连路径会不会「打架」？
代码定位：`MqttHostedService.cs:44-46`；`MqttConnectionOptions.cs:45-47`；`MqttClientWrapper.cs:371-381`。

**Q4.4 ★** 事件退订发生在哪两处？`ExecuteAsync` 的 finally 与 `Dispose` 双重退订各自防什么？
代码定位：`MqttHostedService.cs:51-56`、`:62-66`。

## 五、HTTP 客户端（HttpClientWrapper）

**Q5.1 ★★** 接口注释写「基于 IHttpClientFactory + Polly」，实现却是直接 `new HttpClient`——两者差异是什么？当前 Singleton 长命实例下有什么隐患（DNS、socket 池）？
代码定位：`HTTP/IHttpClient.cs:4-8`；`HTTP/HttpClientWrapper.cs:30-35`。

**Q5.2 ★★** Polly 重试的 `ShouldHandle` 覆盖哪些失败？`TaskCanceledException` 可能是哪两种原因？当前代码能否区分「HttpClient 超时」与「调用方取消」？不区分的后果？
代码定位：`HttpClientWrapper.cs:50-60`。

**Q5.3 ★★** `SendAsync` / `UploadAsync` 的 catch 一律返回 `OperationalError.Timeout`，即使异常是 DNS / 连接拒绝 / 序列化错误——这个命名合理吗？调用方如何区分失败原因？
代码定位：`HttpClientWrapper.cs:89-93`、`:110-114`。

**Q5.4 ★★** HTTP 三态状态机如何判定 Faulted？`_consecutiveFailures` 与 `MaxRetries` 的关系？为什么成功时清零并置 Connected？Faulted 后计数会怎样？
代码定位：`HttpClientWrapper.cs:151-172`。

**Q5.5 ★★** 为什么 HTTP 状态机没有 Connecting / Reconnecting 状态？与 MQTT 五态状态机差异的根因是什么？
代码定位：`HTTP/HttpConnectionState.cs`；`MQTT/MqttConnectionState.cs`。

**Q5.6 ★★** `BearerToken` 在构造时一次性写入 `DefaultRequestHeaders`——token 过期后会发生什么？401 会触发重试或 Faulted 吗？当前设计的前提与演进方向？
代码定位：`HttpClientWrapper.cs:37-39`；`HttpConnectionOptions.cs:19-21`；`HttpClientWrapper.cs:50-60`（401 不在 ShouldHandle）。

**Q5.7 ★** `HealthCheckAsync` 如何复用 `SendAsync`？`HealthPath` 为空时的默认值？
代码定位：`HttpClientWrapper.cs:119-127`。

## 六、DI 与配置

**Q6.1 ★★** `AddNitroMqtt` 自动生成 ClientId 的格式是什么？ADR-006 P1-1 修复了什么 bug？8 位 GUID 后缀的唯一性够吗？
代码定位：`MqttServiceCollectionExtensions.cs:13-30`；`notes/ADR/forwarder/ADR-006-transport-mqtt-optimization.md` P1-1；`tests/NitroGateway.IntegrationTests/MqttClientWrapperTests.cs`（AddNitroMqtt_AutoClientId_IsUniqueAndPrefixed）。

**Q6.2 ★★** `MqttConnectionOptions` 属性为什么全部是 `init`？不可变的意义？`with` 表达式怎么用？`ConfigurationBinder` 绑定 init 属性的兼容性靠什么验证？
代码定位：`MqttConnectionOptions.cs`；`MqttClientWrapperTests.HostPort_AreImmutable`、`AddNitroMqtt_AutoClientId_IsUniqueAndPrefixed`（ADR-006 P3-5）。

**Q6.3 ★** `KeepAliveSeconds` 为什么夹紧到 [5, 3600]？0 / 负值会怎样？
代码定位：`MqttConnectionOptions.cs:31-35`；`MqttClientWrapperTests.KeepAliveSeconds_IsClampedTo5_3600`。

**Q6.4 ★★** `IMqttClient` 为什么注册为 Singleton？它内部有哪些可变状态？如果注册成 Scoped 会怎样？`ForwarderEngine` 每轮 `CreateScope` 解析到的是什么？
代码定位：`MqttServiceCollectionExtensions.cs:27-28`；`ForwarderEngine.cs:114-117`。

**Q6.5 ★★** `AddNitroHttp` 没有注册 HostedService——HTTP 断线（Faulted）后靠什么恢复？这是设计缺口还是刻意为之？MQTT 为什么就必须有监视循环？
代码定位：`HTTP/HttpServiceCollectionExtensions.cs:10-17`；`HttpClientWrapper.cs:151-172`。

## 七、可观测性与健康检查

**Q7.1 ★★** `PublishAsync` 的 Activity 打了哪些标签？哪些路径置 Error 状态？`NitroMetrics.MqttState` 表达什么、何时更新？
代码定位：`MqttClientWrapper.cs:144-192`、`:260-274`（NitroMetrics.MqttState）；`src/NitroGateway.Telemetry/Tracing/GatewayActivities.cs`。

**Q7.2 ★★** `MqttHealthCheck` 的三档映射规则？`DisposeAsync` 为什么先置 Disconnected 再拆线（ADR-006 P3-4）？
代码定位：`src/NitroGateway.Webapi/HealthChecks/MqttHealthCheck.cs:16-25`；`MqttClientWrapper.cs:234-258`。

**Q7.3 ★★** `StatusController` 与 `DeviceStatusDispatcher` 分别如何暴露 MQTT 连接状态？两者的信息形态差异？前端拿到什么？
代码定位：`src/NitroGateway.Webapi/Controllers/StatusController.cs`（MqttState / MqttConnected）；`Hubs/DeviceStatusDispatcher.cs`（IMqttStateListener 实现）。

## 八、场景诊断与演进（深水区）

**Q8.1 ★★★** 诊断题：broker 重启 5 分钟后恢复。按时间线描述你在「日志 / Prometheus 指标 / 健康检查 / Forwarder 行为 / 转发缓冲」五个观察点分别会看到什么？哪些环节不需要人工介入？
代码定位：`MqttClientWrapper.cs:307-369`；`MqttHostedService.cs:25-57`；`ForwarderEngine.cs:97-121`（2026-08-22 删 AIMD，无节流状态）。

**Q8.2 ★★★** 缺陷题：`_subscriptions` 字典「只增不减」，且重放订阅失败只记 Warning 不重试——各自的后果是什么？给出最小修复方案（考虑接口只增不删的约束）。
代码定位：`MqttClientWrapper.cs:203-215`、`:395-407`。

**Q8.3 ★★★** 缺陷题：HTTP 侧 Faulted 后没有恢复机制（无监视循环、无重连状态），与 MQTT 形成对比。最小修复方案是什么？为什么注释里写的 IHttpClientFactory 没有用上？
代码定位：`HttpClientWrapper.cs:30-35,151-172`；`HttpServiceCollectionExtensions.cs`。

**Q8.4 ★★★** 演进题：要实现「多 broker 配置」「动态 token 刷新」「命令消息下行」三个需求，分别需要在哪些文件做最小改动？会触及哪些既有设计约束（接口只增不删、Singleton 生命周期、Channel 丢弃策略）？
代码定位：`IMqttClient.cs`、`MqttConnectionOptions.cs`、`MqttServiceCollectionExtensions.cs`、`HttpClientWrapper.cs`、`MqttClientWrapper.cs:276-305`。

**Q8.5 ★★★** 设计题：`PublishAsync` 未连接时立即返回 Unavailable，Forwarder 跳过本轮；如果改成「在内部排队等待连接」，会引入什么风险？结合本地 SQLite 缓冲已有的 at-least-once 机制讨论职责边界。
代码定位：`MqttClientWrapper.cs:144-192`；`src/NitroGateway.Forwarder/Forwarder.cs`（Dequeue→Publish→Commit）。
