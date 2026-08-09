# Transport 模块面试题（answers）

参考答案只给要点，能展开讲、能指到代码行才算吃透。

## 一、模块定位与抽象设计

**A1.1** 职责边界：Transport 只负责「发出去 / 收进来」，不关心消息内容、不负责序列化（序列化是 Forwarder 的职责，Payload 统一 `byte[]`）。拆成两个子项目：MQTT 依赖 MQTTnet 包，HTTP 用内建 `System.Net.Http` 零额外依赖，可独立打包、独立演进。对上层暴露统一约定（OperationResult、状态机、IAsyncEnumerable），让 Forwarder 等消费方不感知协议差异。

**A1.2** 约定：所有异步操作返回 `OperationResult`，失败不抛异常（CancellationToken 取消除外），调用方用 `IsSuccess / Error` 结构化处理。Forwarder 因此可以安全地「Publish 失败 → MarkFailed → 下轮重试」，不会因 broker 抖动抛异常打崩转发循环。实现内部仍有 try-catch 把异常转成结果，所以调用方基本无需 catch。

**A1.3** 事件回调的问题：订阅者执行时机不可控、异常会直接传播进 MQTTnet 回调、无法背压。`IAsyncEnumerable` + Channel：生产者（MQTTnet 回调线程）写 Channel，消费者 `await foreach` 拉取，天然支持取消与背压语义。注意现状：Channel 虽配 `FullMode.Wait`（背压本意），但回调里实际用 `TryWrite`（满即丢），背压并未真正生效——这是 P3-1 的取舍，见 Q3.1/Q3.2。

**A1.4** 数据载体不依赖 ASP.NET Core：Transport.HTTP 是独立类库，可被非 Web 宿主复用；避免框架类型泄漏到上层；`HttpResponse.IsSuccessStatusCode` 与 `System.Net.Http` 语义一致（2xx），上层无需引框架即可判断。

**A1.5** 关系：`StateChanged` 是 wrapper 的同步事件（当前真正生效的通道，被 MqttHostedService 用于日志）；`IMqttStateListener` 是 Transport 定义的异步监听契约，`DeviceStatusDispatcher` 实现了它以便把 MQTT 状态推进 SignalR 出口，但全仓库没有调用点——「定义了未接线」。这说明该接口是预留契约（与 Messages 管道同样属于「留给未来」），答题时指出这一点即加分：注册不代表生效，要查调用方。

## 二、MQTT 连接状态机与重连

**A2.1** 状态迁移（每个转移都有代码行支撑）：
- 初始 `Disconnected`（字段默认值，`:36`）。
- `ConnectAsync` 开头：若已 Connected 直接 Success（幂等）；否则 `SetState(Connecting)`（`:75-77`）。
- 连接成功：`Connected` + `_reconnectCount=0` + 重放订阅（`:87-90`）。
- 首连失败 / 连接异常：`HandleConnectFailure` → 配置了自动重连则启动循环（循环内置 `Reconnecting`），否则落 `Disconnected`（`:111-115`、`:383-393`）。
- 意外断开事件：仅当 `State==Connected` 时 `StartReconnectLoop`（循环内置 `Reconnecting`）（`:307-328`、`:335-337`）。
- 重连循环耗尽：`Faulted`（`:355-357`）。
- 主动断开：`DisconnectAsync` → 先 `CancelReconnect` → `Disconnected`（`:119-123`）。
- `DisposeAsync`：先置 `Disconnected` 再拆线（`:234-258`）。
- `SetState` 幂等：状态相同不触发事件、不更新指标（`:260-274`）。

**A2.2** 公式：第 N 次重连等待 `min(BaseMs * 2^(N-1), MaxIntervalMs)`，默认 1000ms 起步、封顶 30s。`MaxReconnectAttempts=0` 语义是「不自动重连」：首连失败落 Disconnected、意外断开不启动循环、MqttHostedService 也不兜底——三个分支都为 0 做了专门判断。

**A2.3** 保护「任意时刻只有一个重连循环」。并发入口：① `ConnectAsync` 失败路径（HandleConnectFailure）② 意外断开事件（OnDisconnectedAsync）。两者可能几乎同时触发（连接失败也会伴随断线事件）。不加互斥会启动两个循环：重连计数错乱、并发 Connect 到 broker、状态来回横跳。`StartReconnectLoop` 锁内检查置位、`TryReconnectAsync` finally 锁内复位，保证单实例。

**A2.4** 事件时序不可靠：MQTTnet 的 `DisconnectedAsync` 在首连被拒 / 某些异常路径下可能不触发或时序不确定，导致「连接失败但没有恢复路径」的僵尸态（ADR-006 P1-3 教训）。所以首连失败在 `ConnectAsync` 内部确定性启动重连，不依赖事件；事件只负责「已连接后的意外断开」这一种场景。

**A2.5** `MaxReconnectAttempts==0`：尊重「不自动重连」语义，落 Disconnected 结束。`State==Connected`：只有「已连接后意外断开」才需要启动快速重连；Reconnecting（循环已在跑）不重复启动，Disconnected/Faulted（已放弃或主动断开）不在此重启（前者由首连失败路径或监视循环负责）。

**A2.6** `CleanStart` 让会话不持久，断开即清空 broker 端订阅，重连成功后必须重放。`SubscribeAsync` 成功后在 `_subscriptions` 记录 topic→qos（锁保护），`ConnectAsync` 成功后 `ReplaySubscriptionsAsync` 遍历重放。缺陷：字典只增不减（接口没有 UnsubscribeAsync，也没有清理时机）；重放失败只 LogWarning，订阅会缺失到下一次重连才有机会补上。

**A2.7** finally：释放并置空 `_reconnectCts`、锁内复位 `_reconnectLoopActive`。三条退出路径（成功 / 耗尽 Faulted / 取消 return）都走 finally。不释放：CTS 残留到下一次断开才被清理，可能重复 Cancel 已释放对象；不复位标记：后续断开再也不会启动重连（永久失活）。

**A2.8** 重置计数让下一次断线的退避从 BaseMs 重新开始。不清零：broker 反复抖动时计数一路累加，退避越拉越长，且很快撞上 `MaxReconnectAttempts` 进 Faulted，把「短暂抖动」误判成「永久故障」。

**A2.9** 返回 `OperationalError.Unavailable`（MQTT 未连接）。快速失败而非阻塞等待：若 PublishAsync 挂起，转发单批会长时间卡住，出队停滞、线程堆积。配合 `ForwarderEngine` 的「State != Connected 跳过本轮」（`:118-121`），未连接时根本不出队，数据留在 SQLite 缓冲里，由重连成功后的轮次排空。

**A2.10** 先取消重连循环，消除「断开瞬间循环的 delay 到期又去 Connect」的竞争窗口；若先断开，DisconnectAsync 返回后可能被循环重新拉回 Connected，破坏主动断开语义。

## 三、消息管道与 QoS

**A3.1** `Channel.CreateBounded<MqttMessage>(10_000)` + `FullMode.Wait`（写满应阻塞——背压语义）。但回调里用 `TryWrite`：满时返回 false，记 Warning 丢弃消息。原因：MQTTnet 回调是同步事件，`WriteAsync` 阻塞会卡住 broker 收包线程；当前无下行消费者，丢弃 + 告警作为兜底。风险：若未来有命令消息，会静默丢弃（有日志但无重试）。

**A3.2** 注释（P3-1）明确：未来命令下行落地时，命令类消息应改用 `WriteAsync` 阻塞写（真背压）或独立小容量队列，避免丢弃。当前通道无消费者，丢弃代价低，故保留现状。

**A3.3** `ReadOnlySequence<byte>` 可能跨多个 segment，代码遍历 `segment.Span.CopyTo` 拷贝进新 `byte[]`。不能直接持有引用：底层 buffer 由 MQTTnet 复用，回调返回后可能被回收 / 改写；拷贝保证消息生命周期独立于回调。

**A3.4** `Success` 与 `NoMatchingSubscribers`。后者表示 broker 已处理消息但无订阅者匹配，按协议不算失败；算成功可避免无意义重试风暴。坑：云上无人订阅时 Forwarder 也会 Commit 删除本地数据——消息「发出去了但没人收」，业务上可能等于丢数据，需要云侧订阅监控配合。

**A3.5** 两层叠加：① 本地 SQLite 缓冲两阶段——Dequeue 移出 → Publish 成功才 Commit 删除（失败 MarkFailed 进重试/死信）；② MQTT QoS1 保证 broker 至少收到一次。重复窗口：Publish 成功但 Commit 前崩溃 → 重启后批次重新入队重发；Publish 超时但 broker 实际已收 → 重试重发。丢数据窗口极小：仅当 Commit/死信逻辑自身出错。云侧必须幂等（设备号 + 时间戳去重）配合 at-least-once。

## 四、MqttHostedService 监视循环

**A4.1** 分工：wrapper 负责「已连接后意外断开」的快速指数退避重连 + 首连失败重连；但退避耗尽后进入终态 Faulted，wrapper 自身不再尝试。`MqttHostedService` 是兜底监视器：周期检查 Faulted（或 Disconnected 且配置了自动重连）时主动 `ConnectAsync`，broker 长时间离线后恢复无需重启网关。

**A4.2** 条件：`Faulted`（快速重连已放弃）或 `Disconnected && MaxReconnectAttempts > 0`（首连失败的落点）。加 MaxReconnectAttempts>0：=0 语义是「不自动重连」，监视循环不越权替用户做决定。

**A4.3** 周期用 `ReconnectMaxIntervalMs`（默认 30s）：与 wrapper 内部退避上限一致，避免比内部更频繁的空转。不会打架：`StartReconnectLoop` 单实例互斥 + `ConnectAsync` 幂等（已 Connected 直接成功），且 wrapper 进 Faulted 后内部循环已结束，只剩监视循环在尝试。实际节奏：断线初期 wrapper 快速退避 → 耗尽进 Faulted → 监视循环每 30s 兜底。

**A4.4** `ExecuteAsync` 的 finally 退订保证正常/异常退出都清理；`Dispose` 再退订一次，防 ExecuteAsync 未自然退出时泄漏（重复订阅会导致状态日志重复打印）。双重退订是幂等保险。

## 五、HTTP 客户端

**A5.1** 偏差：注释写 IHttpClientFactory，实现直接 `new HttpClient`（Singleton 长命实例）。差异：工厂管理 handler 轮换与连接池生命周期，直接 new 则单 HttpClient 长期复用——DNS 变更不生效（除非重启）、连接池耗尽时无自动恢复。当前场景（单云端点 + 长命单例）可接受，但属于「实现与文档不一致」，重构方向是改用工厂 + 类型化客户端。

**A5.2** 覆盖：`HttpRequestException`（网络层）、`TaskCanceledException`（超时/取消）、`HttpResult >= 500`（服务器错误）。陷阱：`TaskCanceledException` 既可能是 `HttpClient.Timeout`（应重试）也可能是调用方 `ct` 取消（不应重试）；当前代码无法区分，ct 取消也可能被重试，延长取消响应时间。可讨论 .NET 8+ `TimeoutRejectedException` 区分方案。

**A5.3** 所有异常统一映射 `OperationalError.Timeout`：命名不严谨（DNS、连接拒绝、序列化错误都不是超时），丢失了错误类型信息，调用方只能靠日志定位。设计动机可能是「上传/转发失败统一走重试/死信路径」，但至少应区分 General / Timeout / Unavailable 类别。

**A5.4** `_consecutiveFailures` 连续失败计数：成功 `OnSuccess` 清零并置 Connected（触发事件）；失败 `OnFailure` 累加，`>= MaxRetries + 1` 置 Faulted（即连续失败数超过重试次数，说明重试也没救）。Faulted 只触发一次事件（`State != Faulted` 判断），之后计数继续累加直到下次成功才清零。

**A5.5** HTTP 是无连接协议：每次请求独立建连，没有「连接中」的持续会话，状态只是「最近通信健康度」（Disconnected 初始 / Connected 最近成功 / Faulted 连续失败）。MQTT 是长连接，有明确的握手、断开、重连过程，所以需要五态。根因：协议模型不同，状态机跟着协议走。

**A5.6** token 构造时一次性写入，之后不刷新：过期后所有请求 401，而 401 不在 Polly `ShouldHandle`（<500 不重试），也不会触发 Faulted（响应正常返回，走 OnSuccess 置 Connected）。前提：token 静态 / 长有效期。演进：TokenProvider 回调 + 每次请求前设置 Authorization + 401 刷新重试。

**A5.7** `HealthCheckAsync` 构造 GET `HttpRequest`（`Path = HealthPath ?? "/health"`），复用 `SendAsync`；结果成功则 Success，失败原样透传 `result.Error`。留空时默认打 `/health`。

## 六、DI 与配置

**A6.1** 格式：`NitroGateway-{MachineName}-{Guid:N[..8]}`。修复前：整串 ClientId 取前 8 位恒为 "NitroGat"，所有实例相同，多实例连同一 broker 会按 MQTT 规范互踢（后连的踢掉先连的，转发互相打断）。8 位十六进制约 32 位熵，同机多实例碰撞概率极低，可接受；更严格场景可加完整 GUID / PID。

**A6.2** 全部 `init`：构造后不可变，避免运行期漂移（Host 改了 Port 没改、热更新错乱）；需要改配置时用 `with` 生成新实例（如 AddNitroMqtt 里 `options with { ClientId = ... }`）。`ConfigurationBinder` 对 init-only 属性兼容（.NET 支持），靠测试验证：`AddNitroMqtt_AutoClientId_IsUniqueAndPrefixed` 断言了 `Host`/`Port` 绑定结果，`HostPort_AreImmutable` 断言 with 不改原实例（ADR-006 P3-5）。

**A6.3** MQTTnet 心跳周期为 0 / 负值会导致保活逻辑异常或失效（KeepAlive 必须为正）；夹紧 [5, 3600] 保证合法：5s 最激进（探测快但开销大），3600s 最宽松（省流量但断线发现慢）。

**A6.4** Singleton 合理：wrapper 持有大量可变状态（State、订阅字典、重连循环、Channel、底层连接），必须全进程共享同一实例，否则每个作用域新建连接、状态互相不一致。若 Scoped：ForwarderEngine 每轮 `CreateScope` 都会拿到新 wrapper，连接反复建立/销毁、订阅丢失、健康检查看到的不是转发用的那个实例。ForwarderEngine 每轮 CreateScope 解析的是 IForwarder 等服务，但 IMqttClient 在根容器注册，作用域内解析到的仍是同一个单例。

**A6.5** 目前没有恢复机制：HTTP 每次请求独立建连，Faulted 只是健康标记，下一次成功请求自然恢复（OnSuccess 重置）。严格说 HTTP 不需要「重连」，但也没有主动探测（HealthCheckAsync 存在却无人调度），低流量时 Faulted 会长期悬挂。MQTT 是长连接，断线后连接对象已死，必须显式重连 + 监视循环。可讨论：给 HTTP 加周期健康探测的演进。

## 七、可观测性与健康检查

**A7.1** `PublishAsync` 起 Activity（`GatewayActivities.MqttPublish`），打 `MqttTopic` 标签；未连接 / 发布失败 / 异常三条路径置 `Error` + `ErrorMessage` 标签。`NitroMetrics.MqttState` 是 Prometheus gauge，`SetState` 时写入当前状态枚举值，监控面板可画连接状态变化曲线。

**A7.2** 映射：Connected → Healthy；Connecting / Reconnecting → Degraded；Disconnected / Faulted → Unhealthy。`DisposeAsync` 先置 Disconnected 再拆线：关闭窗口内健康检查立即转不健康，避免「进程已退出但健康检查还短暂报 Healthy」的假阳性（ADR-006 P3-4）。

**A7.3** `StatusController` 暴露 `MqttState`（枚举字符串）与 `MqttConnected`（bool），供状态页 / 调试接口查询；`DeviceStatusDispatcher` 实现 `IMqttStateListener`，本意是把状态变化推 SignalR 给前端实时订阅——但接口无人调用（见 A1.5），所以前端目前拿不到实时 MQTT 状态推送，只能轮询 StatusController。答题时指出「接口已定义、接线未完成」即为加分观察。

## 八、场景诊断与演进

**A8.1** 时间线（broker 重启 5 分钟）：
- t0 断线：wrapper `OnDisconnectedAsync`（ClientWasConnected=true → LogWarning），State → Reconnecting，指数退避 1s / 2s / 4s……；健康检查 Degraded；`NitroMetrics.MqttState` 变为 Reconnecting。
- 断线期间：ForwarderEngine 每轮看到 State != Connected 跳过，缓冲积压；超 1000 批后每 60s 一条积压告警；Throttle 持续失败收缩 batch（若期间有尝试）。
- 退避耗尽（默认 10 次）：State → Faulted（LogError），健康检查 Unhealthy；MqttHostedService 接管，每 30s 尝试一次 ConnectAsync。
- broker 恢复：监视循环下一次尝试成功 → Connected（`_reconnectCount` 重置）→ 重放订阅（如有）→ ForwarderEngine 恢复排空，Throttle 随成功逐步放大 batch，缓冲回落到零。
- 无需人工介入：重连全程自动；只有 MaxReconnectAttempts=0 或离线时间超过缓冲/死信策略承受范围才需人工。

**A8.2** 后果：① 字典只增不减——订阅被业务废弃后，重连仍重放僵尸订阅，产生无谓消息流；② 重放失败只 Warning——本轮订阅缺失且无重试，若 broker 此后一直正常，缺失会永久持续到下一次断线。最小修复：新增 `UnsubscribeAsync`（接口只增不删，新方法）+ 成功后从字典删除；重放失败时标记待重放，在每次连接成功 / 定时任务中重试，或把重放失败视为连接失败触发再次重连。

**A8.3** 后果：HTTP Faulted 后依赖「下一次业务请求」自然恢复，低流量 / 空闲期会长期悬挂；401 等非 5xx 不重试也不判 Faulted，状态与真实可用性脱节。最小修复：注册一个轻量 HostedService 周期性调 `HealthCheckAsync`，Faulted 时主动探测并恢复 Connected（HealthPath 已支持）；或把 401 纳入 token 刷新逻辑。IHttpClientFactory 没用的原因：早期实现简化直接 new，且工厂化需要改构造函数注入，与当前 Singleton 长命实例的取舍一致——属于技术债而非 bug。

**A8.4** 最小改动：
- 多 broker：`MqttConnectionOptions` 支持集合 + 新增命名注册（如 `AddNitroMqtt(name, config)` 或 MqttClientFactory），IMqttClient 接口只增不删，消费方按名字解析；
- 动态 token：`HttpClientWrapper` 增加 TokenProvider 委托，每次请求前刷新 Authorization（不再构造时一次性写入），可选 401 刷新重试；
- 命令下行：`SubscribeAsync` 已支持订阅，接入 `Messages` 消费者（Forwarder 或新 HostedService），并把 `OnMessageReceivedAsync` 的 TryWrite 改为 WriteAsync / 独立队列（P3-1 注释已预留方向）。
- 约束：接口只增不删 → 新能力用新方法/新接口；Singleton 生命周期 → 多实例必须走命名注册或工厂，不能改生命周期。

**A8.5** 排队等待的风险：PublishAsync 挂起 → Forwarder 单批卡死 → 出队停滞；内存队列无限增长 → OOM；超时/取消语义复杂化。更重要的是职责重叠：本地 SQLite 缓冲（持久化、两阶段、死信）已经是「排队」，把等待再放内存队列是重复排队——进程崩溃时内存队列数据全丢，反而破坏 at-least-once。当前设计把「等待」交给持久缓冲（快速失败 + 跳过本轮），PublishAsync 保持无状态快速返回，职责清晰、崩溃安全。