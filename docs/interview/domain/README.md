# Domain 模块面试题集

目的：通过自问自答吃透 `src/NitroGateway.Domain`（核心领域模型层）。题目全部基于**当前代码真实实现**编写，含代码定位与参考答案，可自测、可互考。

## 使用方法

1. 按难度递进刷题：先答 `questions.md`，能写下来/讲清楚算过。
2. 每题都附「代码定位」；答不上或不确定就去看对应代码 + XML 注释 + 测试，再回来答。
3. 对照 `answers.md` 自检。参考答案只给要点，面试时能展开讲才算吃透。
4. 难度标记：★ 基础（边界/数据流）· ★★ 进阶（设计意图/细节）· ★★★ 深水（权衡/缺陷/演进，面试加分项）。

## 建议学习路径

```
模块定位（依赖边界）→ ProtocolIdentifier / DeviceConnection（值对象）
→ DevicePoint / DataType（点位定义）→ Device（聚合）
→ PointSnapshot / QualityCode（运行时快照）→ MeasurementRecord / BatchMeasurements（测量模型）
→ PointStoredEvent / IPointStoredSink（领域事件）→ IProtocolDriver / DriverCapability（协议抽象）
→ 校验与不变量（PointManager.ValidateAsync）→ 开放题
```

## 代码索引

| 组件 | 文件 | 一句话职责 |
| --- | --- | --- |
| 设备实体 | `Devices/Device.cs` | 聚合根：设备基本信息 + 点位集合（只读暴露 + 增删方法） |
| 点位定义 | `Devices/DevicePoint.cs` | 点位静态配置：地址/类型/访问权限/扫描间隔/死区/缩放 |
| 连接参数 | `Devices/DeviceConnection.cs` | 端点/超时/重试 + 协议特有参数字典 |
| 协议标识 | `Devices/ProtocolIdentifier.cs` | 值对象：协议名 + 方言，忽略大小写相等 |
| 数据类型 | `Devices/DataType.cs` + `DataTypeExtensions.cs` | 11 种标量类型 + Modbus 寄存器数映射 |
| 访问权限 | `Devices/PointAccess.cs` | ReadOnly / WriteOnly / ReadWrite |
| 设备状态 | `Devices/DeviceStatus.cs` | Unknown / Online / Offline / Error / Maintenance |
| 点位快照 | `Devices/PointSnapshot.cs` | 运行时不可变快照：原始值 + 工程值 + 质量（自描述） |
| 数据质量 | `Devices/QualityCode.cs` | Good / Uncertain / Bad（OPC UA 规范） |
| 校验错误 | `Devices/PointValidationError.cs` | 字段级校验错误（Field + Message） |
| 测量记录 | `Measurements/MeasurementRecord.cs` | 可脱离点位定义独立存储/传输的记录 |
| 测量批次 | `Measurements/BatchMeasurements.cs` | 一轮扫描的完整数据 + 成功/失败计数 |
| 聚合类型 | `Measurements/AggregateKind.cs` | 时序降采样聚合枚举（当前未接线） |
| 领域事件 | `Events/PointStoredEvent.cs` + `Events/IPointStoredSink.cs` | 存储完成后的事件契约（观察者） |
| 协议驱动 | `Protocols/IProtocolDriver.cs` | 协议驱动统一接口：连接/读写/批量/Ping，全返回 OperationResult |
| 原始值 | `Protocols/RawPointValue.cs` | 已解码未缩放的值（驱动边界产物） |
| 能力声明 | `Protocols/DriverCapability.cs` | 批量/订阅/上限能力，驱动采集策略 |
| 驱动状态 | `Protocols/DriverState.cs` | Disconnected / Connecting / Connected / Faulted |
| 连接字段 | `Protocols/ConnectionField.cs` | 前端表单元数据（当前未接线） |

## 跨模块依赖（答题时需要知道的上下文）

- `OperationResult` / `OperationalError`：Shared 模块，Domain 的唯一依赖；错误码 + 分类 + 严重度
- `PointValuePipeline`：Collection 模块，消费 `DevicePoint`/`RawPointValue`，产出 `PointSnapshot`（缩放 + 死区）
- `DataDispatcher.ToBatchMeasurements`：Collection 模块，快照 → 测量记录转换点
- `SinkDispatcher` / `AlarmHostedService` / `DeviceStatusDispatcher`：订阅 `IPointStoredSink` 的消费者
- `DeviceReader` / `ModbusBatchPlanner`：Collection/Protocol 模块，消费 `IProtocolDriver` / `DriverCapability` / `RawPointValue`
- `PointManager.ValidateAsync`：Device 模块，`PointValidationError` 的消费方
- `Persistence/DomainMapper.cs`：Entity ↔ Domain 双向映射
- 相关测试：`DataTypeExtensionsTests`、`PointValuePipelineTests`、`DataDispatcherTests`、`OperationalErrorTests`

## 注意事项

- **代码是唯一事实来源**。README 与部分 XML 注释存在漂移（README「不依赖任何项目」、`DevicePoint.Deadband`「不触发上报」），已登记 `notes/ADR/ADR-008-domain-doc-drift.md`，答题以代码 + XML 注释为准，题目中也埋了漂移题。
- **预留未接线模型**：`AggregateKind`、`ConnectionField`、`Alarm.Events.PointStoredEvent`（重复模型）目前无消费方，题目要求你搜索证据后自己下结论。
- Domain 没有独立测试工程——它的行为由消费者模块的测试覆盖（Pipeline/Dispatcher/OperationalError 等）。答完所有题目后，试着不看代码把「设备定义 → 驱动解码 → 快照 → 记录 → 事件」的完整数据形态变化画出来——能画出来就是吃透了。
