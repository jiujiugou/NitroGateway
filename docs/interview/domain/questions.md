# Domain 模块面试题

> 难度：★ 基础（边界/数据流）· ★★ 进阶（设计意图/细节）· ★★★ 深水（权衡/缺陷/演进，面试加分项）。每题附「代码定位」，答不出先看代码再看答案。
> 共 8 组 36 题；参考答案见 `answers.md`。

---

## 一、模块定位与分层

**Q1.1 ★** Domain 模块的职责是什么？它包含哪几类成员（实体/值对象/事件/接口）？在「采集 → 存储 → 转发」数据流中它处于什么位置？
代码定位：`src/NitroGateway.Domain` 四个子目录（Devices / Measurements / Events / Protocols）。

**Q1.2 ★★** Domain 的依赖边界：`NitroGateway.Domain.csproj` 引用了谁？README 声称「不依赖任何其他项目」与实现一致吗？（文档漂移题）
代码定位：`NitroGateway.Domain.csproj`；`README.md`；`IProtocolDriver` 的返回值类型。

**Q1.3 ★★** 仓库雷区写着「Storage/、Protocol/Abstraction/ 是纯接口，接口只增不删」。这条规则对 Domain 里的 `IProtocolDriver`、`IPointStoredSink` 意味着什么？新增能力时怎么不破坏已有实现？
代码定位：`Protocols/IProtocolDriver.cs`；`Events/IPointStoredSink.cs`。

**Q1.4 ★★** `OperationResult` / `OperationalError` 为什么放在 Shared 而不是 Domain？如果把它们搬进 Domain，会破坏什么？

---

## 二、设备模型（Device / DevicePoint / DeviceConnection）

**Q2.1 ★★** `Device` 的 `Points` 为什么暴露 `IReadOnlyCollection` 而不是 `List`？`AddPoint` / `RemovePoint` 的封装意图？现有实现有哪些缺口（重复 ID、删除不存在、线程安全）？
代码定位：`Devices/Device.cs`。

**Q2.2 ★** `DevicePoint` 有哪些字段？逐一说出含义与默认值。
代码定位：`Devices/DevicePoint.cs`。

**Q2.3 ★** `DevicePoint.Address` 的格式由谁解释？举出三种协议的地址示例。Domain 层为什么不解析它？
代码定位：`DevicePoint.Address` 注释。

**Q2.4 ★★** `DataType` 枚举有哪 11 种？`RegisterCount` 是做什么用的？对 `String` 返回 2、未知枚举返回 1，各有什么隐患？
代码定位：`Devices/DataType.cs`；`Devices/DataTypeExtensions.cs`；测试 `DataTypeExtensionsTests.cs`。

**Q2.5 ★** `PointAccess`、`Enabled`、`ScanIntervalMs`（0 的含义）分别是什么语义？
代码定位：`Devices/PointAccess.cs`；`Devices/DevicePoint.cs`。

**Q2.6 ★★** `DeviceStatus`（五态）和 `DriverState`（四态）为什么是两套状态？各自回答什么问题？谁负责从采集结果推导 `DeviceStatus`？
代码定位：`Devices/DeviceStatus.cs`；`Protocols/DriverState.cs`。

**Q2.7 ★★** `DeviceConnection` 有哪些默认值？`Parameters` 字典存在的意义？配合 `ConnectionField` 元数据能解决什么问题？`ConnectionField` 当前有消费方吗？
代码定位：`Devices/DeviceConnection.cs`；`Protocols/ConnectionField.cs`。

---

## 三、点位快照（PointSnapshot / QualityCode）

**Q3.1 ★★** `PointSnapshot` 为什么用 `record` + `init`（不可变）？不可变对多线程流转（采集 → 分发 → 存储 → 转发）有什么价值？
代码定位：`Devices/PointSnapshot.cs`。

**Q3.2 ★★** 为什么 `PointSnapshot` 要冗余 `PointName` 和 `DataType`？「自描述」解决什么问题？`DataType` 冗余字段是哪次修复引入的（背景）？
代码定位：`PointSnapshot.DataType` 注释；`DataDispatcher.ToBatchMeasurements` 注释；ADR-001 P1-5。

**Q3.3 ★** `RawValue` 和 `Value` 的区别？缩放公式是什么？`RawValue` 保留的意义？
代码定位：`PointSnapshot.RawValue` / `Value` 注释；`PointValuePipeline.ConvertSingle`。

**Q3.4 ★★** `QualityCode` 三态（Good / Uncertain / Bad）各自语义？遵循什么规范？各举一个产生场景。
代码定位：`Devices/QualityCode.cs`。

**Q3.5 ★★** `ErrorMessage` 与 `Quality` 的约定关系？这个不变量是「代码强制」还是「注释约定」？Pipeline 缩放失败时产出什么？
代码定位：`PointSnapshot.ErrorMessage` 注释；`PointValuePipeline.ConvertSingle` catch 分支。

---

## 四、测量模型（Measurements）

**Q4.1 ★★** `MeasurementRecord` 与 `PointSnapshot` 是什么关系？为什么需要两套几乎相同的模型？各自的适用场景？
代码定位：`Measurements/MeasurementRecord.cs` 类注释；`Devices/PointSnapshot.cs` 类注释。

**Q4.2 ★★** `Timestamp` 和 `ReceivedAt` 分别代表什么时间？两者的差值能用来做什么分析？
代码定位：`MeasurementRecord.Timestamp` / `ReceivedAt` 注释。

**Q4.3 ★** `BatchMeasurements` 的 `SuccessCount` / `FailCount` 为什么做成计算属性而不是存储字段？计数口径是什么？
代码定位：`Measurements/BatchMeasurements.cs`。

**Q4.4 ★★★** `AggregateKind`（Avg/Max/Min/Sum/Count/First/Last）是干什么用的？它在当前仓库里有消费方吗？（搜索代码证据）一个「未接线」的领域模型，你认为该保留还是删除？
代码定位：`Measurements/AggregateKind.cs`；`rg "AggregateKind" src web tests`。

---

## 五、领域事件（Events）

**Q5.1 ★★** `IPointStoredSink` + `PointStoredEvent` 是什么设计模式？订阅方有哪些（列出至少两个）？为什么接口定义在 Domain？
代码定位：`Events/IPointStoredSink.cs`；`Events/PointStoredEvent.cs`。

**Q5.2 ★★** 事件从发布到消费的完整链路（DataDispatcher → SinkDispatcher → Sink）？Channel 容量与满时策略？单个 Sink 异常的影响？
代码定位：`DataDispatcher.DispatchAsync`；`SinkDispatcher.cs` 类注释与 `ExecuteAsync`。

**Q5.3 ★★★** `src/NitroGateway.Alarm/Events/PointStoredEvent.cs` 与 `Domain.Events.PointStoredEvent` 结构相同。它被谁使用？（搜索证据）这是设计还是死代码？
代码定位：`Alarm/Events/PointStoredEvent.cs`；`rg "NitroGateway.Alarm.Events" src`。

---

## 六、协议抽象（Protocols）

**Q6.1 ★** `IProtocolDriver` 的接口全貌：状态、能力、连接、读写、批量、Ping 各自的作用？`PingAsync` 的设计意图？
代码定位：`Protocols/IProtocolDriver.cs`。

**Q6.2 ★★★** 为什么「所有操作返回 OperationResult，不抛异常」？`OperationalError` 有哪些结构化字段和工厂方法？什么错误仍然应该抛异常？
代码定位：`Shared/OperationResult.cs`；`Shared/OperationalError.cs`。

**Q6.3 ★★** `RawPointValue` 的边界：它「解码了但没缩放」——协议解码和工程缩放各自的职责归属？Modbus / OPC UA 驱动各负责什么转换？
代码定位：`Protocols/RawPointValue.cs` 类注释。

**Q6.4 ★★** `DriverCapability` 解决什么问题？`SupportsBatchRead` / `SupportsSubscription` / `MaxBatchSize`（0 的含义）如何影响采集引擎的调用策略？
代码定位：`Protocols/DriverCapability.cs`；`DeviceReader.ReadDeviceAsync`。

**Q6.5 ★** `DriverState` 状态机（Disconnected → Connecting → Connected → Faulted）？Faulted 之后谁负责重连？
代码定位：`Protocols/DriverState.cs`。

**Q6.6 ★★** `ConnectionField` 元数据（Key/Label/Type/Placeholder/Default/Options/Required）想解决什么问题？当前有消费方吗？它与 `DeviceConnection.Parameters` 怎么配合？
代码定位：`Protocols/ConnectionField.cs`；`Devices/DeviceConnection.Parameters`。

**Q6.7 ★★** `ProtocolIdentifier` 为什么做成值对象而不是字符串？`Equals` / `GetHashCode` 的细节（大小写、方言）？如果 Equals 与 GetHashCode 规则不一致会怎样？
代码定位：`Devices/ProtocolIdentifier.cs`。

---

## 七、校验与不变量

**Q7.1 ★★** `PointManager.ValidateAsync` 现在校验了什么？`PointValidationError` 的结构（Field + Message）有什么好处？现状漏掉了哪些校验？
代码定位：`Device/PointManager.ValidateAsync`；`Devices/PointValidationError.cs`。

**Q7.2 ★★★** 讨论题：「点位地址格式校验」应该放在哪一层？`IAddressParser` 未接线的代价是什么？（DESIGN.md 的待办）
代码定位：`Device/DESIGN.md` 校验段落；`PointManager.ValidateAsync`。

**Q7.3 ★★★** 盘点 `DevicePoint` / `PointSnapshot` 的不变量：哪些是 `required`/枚举强制的，哪些只是注释约定？「约定型不变量」出错时会在哪一层暴露？
代码定位：`Devices/DevicePoint.cs`；`Devices/PointSnapshot.cs`。

---

## 八、开放与设计权衡

**Q8.1 ★★★** 如果让你重构 Domain，最想改的三处？为什么？（可以从文档漂移、校验、重复模型、并发、未接线模型等方向谈）

**Q8.2 ★★★** 手写题：不查代码，实现一个与 `ProtocolIdentifier` 等价的类（忽略大小写相等、ToString、预置静态实例），再对比现有实现指出差异。

**Q8.3 ★★★** 如果新增一种协议（如 BACnet），Domain 层需要改动什么？「接口只增不删」在这里的价值是什么？
代码定位：`Protocols/IProtocolDriver.cs`；`Devices/ProtocolIdentifier.cs` 预置实例。
