# Forwarder 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 8 组 38 题；参考答案见 `answers.md`。

---

## 一、架构与数据流（Forwarder / DI）

**Q1.1 ★** 从缓冲里有数据到云端 Broker 收到消息，完整调用链是什么？数据形态如何变化？
代码定位：`Forwarder.cs` 全文；`DESIGN.md` 流水线图。

**Q1.2 ★** Forwarder 的边界：哪些事情明确**不做**？各由谁负责？（指令下行、连接管理、存储实现、消费确认…）
代码定位：`README.md`；`IMqttClient.cs`；`IForwardBuffer.cs`。

**Q1.3 ★** `ForwardBatchAsync` 的返回语义：哪些路径返回 Failure？哪些路径即使有失败也返回 Success？为什么？
代码定位：`Forwarder.cs:64-154`。

**Q1.4 ★★** `IMessageSerializer` 抽象的价值？`ContentType` 给谁用？换成 Protobuf 需要改 Forwarder 吗？
代码定位：`IMessageSerializer.cs`；`JsonMessageSerializer.cs`。

**Q1.5 ★★** DI 生命周期：为什么节流器/序列化器/转发器都是 Singleton？引擎每轮为什么要独立 scope？
代码定位：`ForwarderServiceCollectionExtensions.cs`；`ForwarderEngine.cs:113-127`。

**Q1.6 ★★** 单轮实际出队多少批由什么决定？`maxCount`、`throttle.MaxBatchSize`、`MaxDrainPerRound` 三者关系？
代码定位：`Forwarder.cs:69`；`ForwarderEngine.cs:28,127`；`DESIGN.md` v1 决策表（找漂移）。

---

## 二、缓冲与两阶段状态机（IForwardBuffer / SqliteForwardBuffer）

**Q2.1 ★** 缓冲状态机完整描述：Pending / InFlight / DeadLetter 各自含义与迁移路径；Dequeue 为什么「只返回不移除」？
代码定位：`IForwardBuffer.cs`；`SqliteForwardBuffer.cs` 类注释。

**Q2.2 ★★** `DequeueAsync` 为什么 SELECT + UPDATE 必须同一事务？反序列化为什么放到事务外？
代码定位：`SqliteForwardBuffer.cs` DequeueAsync。

**Q2.3 ★★** 出队后反序列化失败的行怎么处理？为什么不能让它留在 InFlight？
代码定位：`SqliteForwardBuffer.cs` RecoverCorruptRowAsync；注释 P0-1②。

**Q2.4 ★★** 进程崩溃后重启，遗留的 InFlight 批次怎么办？恢复失败会怎样？
代码定位：`SqliteForwardBuffer.cs` 构造函数；注释 P0-1①。

**Q2.5 ★★** `MarkFailedAsync` 一次 UPDATE 完成哪些事？为什么 P2-11 要合并往返？
代码定位：`SqliteForwardBuffer.cs` MarkFailedAsync；`notes/ADR/ADR-001-forwarder-data-reliability.md` P2-11。

**Q2.6 ★★** 死信操作的状态约束：Retry / Discard 为什么要求条目处于 DeadLetter？找不到时返回什么？
代码定位：`SqliteForwardBuffer.cs` RetryDeadLetterAsync / DiscardDeadLetterAsync。

---

## 三、失败路径与可靠性（Forwarder 的失败处理）

**Q3.1 ★★** Dequeue 失败为什么必须返回 Failure 并记 Error 日志？Activity 状态怎么置？
代码定位：`Forwarder.cs:71-77`；`ForwarderFailureTests`。

**Q3.2 ★★** 单批 Publish 返回失败：依次做了什么？对方法返回值、后续批次、节流、指标、Activity 各有什么影响？
代码定位：`Forwarder.cs:105-119`。

**Q3.3 ★★** Publish 抛异常（catch Exception）与 Publish 返回失败，两条路径的差异在哪？
代码定位：`Forwarder.cs:105-130`。

**Q3.4 ★★** Commit 失败会发生什么？为什么必须记 Error 日志？
代码定位：`Forwarder.cs:133-142`；注释 P1-3②。

**Q3.5 ★★** MarkFailed 本身失败呢？「卡 InFlight」意味着什么，何时恢复？
代码定位：`Forwarder.cs:157-169`；`SqliteForwardBuffer.cs` 构造函数。

**Q3.6 ★★★** 至少一次（at-least-once）语义下，哪些崩溃窗口会产生重复投递？会不会丢数据？云端如何配合？
代码定位：`Forwarder.cs` 提交逻辑；`IMqttClient.cs` QoS 注释。

---

## 四、AIMD 节流（ForwardingThrottle）

**Q4.1 ★** 节流器两个状态量：初始值、上下限、各自调整规则？
代码定位：`ForwardingThrottle.cs:20-44`；`ForwardingThrottleTests`。

**Q4.2 ★★** 为什么「收紧快、恢复慢」？与 TCP AIMD 的关系？
代码定位：`ForwardingThrottle.cs` 类注释。

**Q4.3 ★★** 为什么节流器必须是 Singleton？如果注册成 scoped 会怎样？
代码定位：`ForwarderServiceCollectionExtensions.cs`；`Forwarder.cs:69`。

**Q4.4 ★★** 节流器为什么不加锁？什么情况下这个假设会失效？
代码定位：`ForwardingThrottle.cs` 类注释（线程安全段落）。

**Q4.5 ★★★** 全局共享节流的副作用：一台设备持续坏消息会怎样？为什么 v1 接受？
代码定位：`ForwardingThrottle.cs` 类注释；`notes/ADR/ADR-001-forwarder-data-reliability.md` P3-14。

**Q4.6 ★★** `ApplyDelayAsync` 的取消语义：等待期间 ct 取消抛什么？谁处理？
代码定位：`ForwardingThrottle.cs:46-50`；`Forwarder.cs:97`；`ForwarderEngine.cs` 停机路径。

---

## 五、引擎与调度（ForwarderEngine）

**Q5.1 ★** PeriodicTimer 驱动：首轮为什么立即执行？do-while 顺序的意义？
代码定位：`ForwarderEngine.cs:72-79`；`ForwarderEngineTests.FirstRound_RunsImmediately_WithoutWaitingFullInterval`。

**Q5.2 ★★** MQTT 未连接为什么跳过本轮？与 Faulted 状态的区别？
代码定位：`ForwarderEngine.cs:119`；`MqttConnectionState.cs`。

**Q5.3 ★★** 积压告警的三条规则（阈值 / 限流 / 重置）？为什么这样设计？
代码定位：`ForwarderEngine.cs:22-26,97-111`；`ForwarderEngineTests`。

**Q5.4 ★★** 单轮异常兜底：什么异常被记录后继续下一轮？什么异常会终止循环？
代码定位：`ForwarderEngine.cs:121-135`。

**Q5.5 ★★** 优雅停机路径：stoppingToken 取消后发生了什么？
代码定位：`ForwarderEngine.cs:64-90`。

---

## 六、序列化与 Topic（JsonMessageSerializer）

**Q6.1 ★★** 序列化选项为什么静态共享？camelCase 从哪来？输出是什么编码？
代码定位：`JsonMessageSerializer.cs`。

**Q6.2 ★★** Topic 模板 `nitrogateway/{deviceId}/measurements` 的消费模型有什么问题？v2 多级 Topic 的动机？
代码定位：`Forwarder.cs:104`；`DESIGN.md` v1 决策表 Topic 行。

---

## 七、可观测性（指标 / Activity / 诊断）

**Q7.1 ★★** 三个 Prometheus 指标分别表达什么？为什么 `ThrottleBatchSize` 也值得暴露？
代码定位：`Forwarder.cs:146-148`；`NitroMetrics.cs`。

**Q7.2 ★★★** 诊断题：Broker 断线 3 小时再恢复，按时间线描述你会在日志 / 指标 / 缓冲状态里看到什么？
代码定位：`ForwarderEngine.cs:97-111`；`ForwardingThrottle.cs`；`SqliteForwardBuffer.cs`。

**Q7.3 ★★★** 陷阱题：QoS1 + 本地两阶段 Commit 能保证云端「恰好一次」吗？为什么？业务上怎么兜底？
代码定位：`IMqttClient.cs` QoS 注释；`Forwarder.cs` 提交顺序。

---

## 八、开放性 / 演进（DESIGN.md）

**Q8.1 ★★★** v2-v5 演进路线排序的理由？为什么 v2 先做 Protobuf + 多 Topic？
代码定位：`DESIGN.md` 演进表。

**Q8.2 ★★★** v4 应用层 ACK：设计要点、与现在 Commit 时机的差异、代价？
代码定位：`DESIGN.md` v1 决策表 Commit 行。

**Q8.3 ★★★** 无限积压：Broker 长时间不可用，本地缓冲持续增长，你会怎么设计容量策略？
代码定位：`SqliteForwardBuffer.cs`；`ForwarderEngine.cs` 积压告警。

**Q8.4 ★★★** 坏消息分类：当前「数据格式错误」与「连接失败」走同一重试路径，有什么问题？怎么优化？
代码定位：`Forwarder.cs:105-130`；`SqliteForwardBuffer.cs` MarkFailedAsync。
