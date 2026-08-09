# Domain 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：README 与部分 XML 注释存在漂移（Q1.2、Q7.3 即漂移题，见 ADR-008）。

---

## 一、模块定位与分层

**Q1.1 职责与位置**
- 四类成员：实体（`Device`）、值对象（`ProtocolIdentifier`、`DeviceConnection`、`PointSnapshot`、`RawPointValue` 等）、领域事件与回调（`PointStoredEvent` / `IPointStoredSink`）、协议抽象（`IProtocolDriver` 及其支撑类型）。
- 位置：采集（Collection）产出快照 → Domain 模型流转 → 存储（Persistence）与转发（Forwarder）消费；快照/记录是跨模块的数据契约，放 Domain 才能让各模块无环引用。
- 依赖规则：不引用任何基础设施/实现模块；`csproj` 仅引用 Shared。

**Q1.2 依赖边界（漂移题）**
- 实际引用 `NitroGateway.Shared`：`IProtocolDriver` 的返回值是 `OperationResult` / `OperationResult<T>`，定义在 Shared。
- README 声称「不依赖任何其他项目」与实现矛盾（ADR-008 P2-1 已登记）。可接受的解释：Shared 是零依赖的通用错误模型，可视为领域基础设施；严格 DDD 可讨论把 `OperationResult` 下沉进 Domain。

**Q1.3 接口只增不删**
- `IProtocolDriver` / `IPointStoredSink` 是已发布契约，多个模块已实现/消费；破坏性修改（删方法、改签名）会让所有实现编译失败或行为变更。
- 新增能力：加新接口、加默认接口方法、或在能力描述（如 `DriverCapability`）上扩展；协议实现隔离在 Protocol 模块，Domain 只定义契约。

**Q1.4 OperationResult 归属**
- Shared 是唯一被 Domain 依赖的「邻居」，Shared 自身零依赖，把它放 Shared 让 Domain 保持纯粹、也让非领域模块（Webapi、Persistence）复用同一错误模型。
- 搬进 Domain：所有引用 Shared 的模块（几乎全部）会反向依赖 Domain，破坏分层；Shared 的读者（如 Webapi 控制器直接构造 OperationalError）会被迫依赖领域层。

---

## 二、设备模型

**Q2.1 Device 聚合**
- `_points` 私有 `List`，对外只暴露 `IReadOnlyCollection` + `AddPoint` / `RemovePoint`：防止外部绕过业务方法直接改集合，保持聚合根封装。
- 缺口：`AddPoint` 不校验重复 `Id`；`RemovePoint` 用 `RemoveAll` 无返回值，删除不存在的 ID 也「成功」；`_points` 是普通 `List`，配置热更新与读取并发时非线程安全。
- 改进方向（讨论）：`AddPoint` 返回结果或抛重复冲突；`RemovePoint` 返回是否删除；必要时换 `ConcurrentDictionary<Guid, DevicePoint>`。

**Q2.2 DevicePoint 字段**
- `Id`（init）、`Name`（required）、`Address`（required，协议地址表达式）、`Description`（可空）、`DataType`、`Enabled`（默认 true）、`Access`（默认 ReadOnly）、`ScanIntervalMs`（0 = 继承设备默认间隔）、`Deadband`（0 = 不启用死区）、`ScaleFactor`（默认 1.0）、`ScaleOffset`（默认 0）。
- 缩放公式：工程值 = RawValue × ScaleFactor + ScaleOffset。

**Q2.3 Address 语义**
- 由各协议驱动/解析器解释，Domain 只承载不解析：Modbus 保持寄存器 `"40001"`；OPC UA NodeId `"ns=3;s=Temperature"`；S7 `"DB1.DBD0"`。
- 理由：地址格式是协议知识，解析逻辑属于 Protocol 模块（`IAddressParser` 待接线，见 Q7.2），Domain 保持协议无关。

**Q2.4 DataType 与 RegisterCount**
- 11 种：Bool、Byte、Int16、UInt16、Int32、UInt32、Int64、UInt64、Float、Double、String。
- `RegisterCount`：返回该类型占用的 Modbus 寄存器数，供批量读规划/地址跨度计算（32 位=2、64 位=4、Float=2、Double=4）。
- 隐患：`String` 返回 2 只是「至少 2 个寄存器」的估算，按它分配缓冲区可能截断长字符串；`_ => 1` 静默兜底会掩盖新增枚举漏配，建议显式抛异常或按最小安全值处理。测试：`DataTypeExtensionsTests`。

**Q2.5 采集语义**
- `PointAccess`：ReadOnly（采集型，如传感器）、WriteOnly（控制型，如继电器）、ReadWrite（如变频器频率设定）。
- `Enabled`：是否参与采集；`DeviceReader` 只取 `Enabled = true` 的点位。
- `ScanIntervalMs`：0 表示继承设备默认间隔；v1 全量按设备间隔采集，DESIGN.md 提及 v2 按点位间隔分组采样。

**Q2.6 两套状态**
- `DeviceStatus`（Unknown/Online/Offline/Error/Maintenance）：业务/健康视角，回答「设备是否可服务、要不要采集与告警」，由 Device 模块 `DeviceHealthMonitor` 从连续成功/失败计数推导（默认连续失败 3 次→Offline，连续成功 3 次→Online）。
- `DriverState`（Disconnected/Connecting/Connected/Faulted）：连接视角，回答「驱动连接处于什么阶段」，是底层事实。
- 两套状态不同步是正常设计：熔断器恢复（探测成功）与健康恢复（连续成功 3 次）之间存在窗口。

**Q2.7 DeviceConnection**
- 默认值：ConnectTimeoutMs=3000、RequestTimeoutMs=5000、RetryCount=3、RetryIntervalMs=1000。
- `Parameters` 字典：承载协议特有参数（如 Modbus `UnitId`、串口 `BaudRate`/`Parity`），让连接配置可扩展而不用改领域模型。
- `ConnectionField`：前端表单元数据（Key/Label/Type/Placeholder/Default/Options/Required），理想用法是「协议声明字段 → 前端自动渲染表单 → 结果存入 Parameters」。
- 现状：`ConnectionField` 在 src/web/tests 无消费方（`rg` 仅命中定义文件）——预留设计，可讨论「未接线元数据的保留成本」。

---

## 三、点位快照

**Q3.1 不可变**
- `record` + `init`：值语义（相等比较）+ 构造后不可改。每次采集生成新实例，旧快照不被意外修改。
- 价值：快照在多线程/多消费者间流转（采集线程 → Channel → 存储/转发/告警/推送），不可变保证读安全，无需防御性拷贝。

**Q3.2 自描述冗余**
- `PointName` / `DataType` 冗余使快照「自描述」：转发 payload 和告警可直接使用，无需反查 `DevicePoint`。
- `DataType` 冗余是 ADR-001 P1-5 修复：此前转发恒按 Float 解析，Bool/Int/String 全部解析错误；`DataDispatcher.ToBatchMeasurements` 改为透传 `s.DataType`。测试：`DataDispatcherTests.DataType_PropagatedToSnapshot` 相关断言。

**Q3.3 RawValue vs Value**
- `RawValue`：驱动解码后的原始值，未经缩放，保留用于现场调试（「PLC 到底返回了什么」）。
- `Value`：工程值 = RawValue × ScaleFactor + ScaleOffset；示例：PLC 返回 Int16=1234、ScaleFactor=0.1 → RawValue=1234、Value=123.4。
- 缩放/死区在 `PointValuePipeline`，驱动不感知业务缩放。

**Q3.4 QualityCode**
- 遵循 OPC UA 数据质量规范三档：Good（正常可信）；Uncertain（来源不确定：传感器老化、通信间歇中断后首次恢复）；Bad（不可信：采集失败、超时、CRC/校验错误）。
- 使用方：`BatchMeasurements.SuccessCount` 按 `Quality == Good` 计数；HealthReporter 按质量统计成功/失败。

**Q3.5 ErrorMessage 约定**
- 约定：Good 时 `ErrorMessage` 为 null，Uncertain/Bad 时应填写描述（如「Modbus 超时」「CRC 校验失败」）。
- 现状是注释约定而非代码强制——构造时没人阻止你「Bad 且 ErrorMessage=null」。
- Pipeline 缩放失败产出：`Quality = Uncertain` + `ErrorMessage = "缩放失败：无法转换为数值"`，单点失败不影响整批。

---

## 四、测量模型

**Q4.1 两套模型**
- `PointSnapshot`：内存流转的短命对象，轻量、无 `Id`/`ReceivedAt`。
- `MeasurementRecord`：可脱离 `DevicePoint` 独立存储/传输，带 `Id`（记录唯一标识）与 `ReceivedAt`（网关接收时间），包含查询与转发所需的全部上下文。
- 转换点：`DataDispatcher.ToBatchMeasurements` 把快照转成记录并组装 `BatchMeasurements`（一轮扫描一个批次）。

**Q4.2 时间戳**
- `Timestamp`：数据源时间（设备本地或 PLC 时间）；`ReceivedAt`：网关收到时间。
- 差值 = 链路/协议传输延迟，可用于链路质量分析（延迟突增往往预示网络劣化）。

**Q4.3 计算属性**
- `SuccessCount` = `Records.Count(r => r.Quality == QualityCode.Good)`；`FailCount` = 总数 − 成功数。
- 计算属性由 Records 实时推导，避免「记录集合改了、计数没同步」的一致性缺陷；代价是每次访问 O(n)，批内记录数不大时可接受。

**Q4.4 AggregateKind（未接线题）**
- 用途：时序降采样/统计查询的聚合类型（Avg/Max/Min/Sum/Count/First/Last），对应时序库聚合 API。
- 现状：src/web/tests 无消费方（`rg` 仅命中定义文件）——预留设计。
- 讨论方向：未接线模型是「为已知演进预留」还是「YAGNI 死代码」？保留成本低、语义明确，可接受；但面试可追问：如果未来降采样走 SQL 而非领域枚举，这个枚举就冗余。

---

## 五、领域事件

**Q5.1 观察者**
- 模式：发布-订阅（观察者）。`DataDispatcher` 发布 `PointStoredEvent`，`SinkDispatcher` 遍历所有 `IPointStoredSink` 通知。
- 订阅方：`AlarmHostedService`（告警评估）、`DeviceStatusDispatcher`（Webapi SignalR 推送）等。
- 接口定义在 Domain：Domain 只声明契约，实现方在各模块，避免 Domain 反向依赖任何消费者。

**Q5.2 事件链路**
- `DataDispatcher.DispatchAsync` → `_sinks.Post(event)` 入有界 Channel（容量 1000）→ `SinkDispatcher.ExecuteAsync` 消费，每个事件创建独立 DI scope，遍历调用所有 Sink。
- 满时策略：丢弃最旧事件 + 警告日志（`DropOldest`），采集热路径不被阻塞。
- 单个 Sink 异常：只记日志，不影响其他 Sink 与后续事件（类注释明确边界）。
- 链路意义：落库/告警/推送全部异步化，慢消费者不阻塞采集循环。

**Q5.3 Alarm.Events.PointStoredEvent（陷阱题）**
- `Alarm/Events/PointStoredEvent.cs` 与 `Domain.Events.PointStoredEvent` 字段完全相同，但没有任何消费方：所有消费者（AlarmHostedService、DeviceStatusDispatcher、SinkDispatcher、DataDispatcher）都 using `NitroGateway.Domain.Events`。
- 结论：重复模型/死代码（worklog 2026-08-07 已标记待确认），建议删除或迁移到 Domain，避免「两个同名事件」的混淆成本。

---

## 六、协议抽象

**Q6.1 接口全貌**
- `State`（当前连接状态）+ `Capability`（能力声明）+ `ConnectAsync` / `DisconnectAsync` / `PingAsync` / `ReadAsync` / `ReadBatchAsync` / `WriteAsync` / `WriteBatchAsync`，继承 `IDisposable`。
- `PingAsync`：连接验证，发最小代价的读请求确认设备可达，用于探测/健康检查，不携带业务数据。
- 调用方（DeviceReader）通过接口操作任意协议设备，不感知协议细节。

**Q6.2 OperationResult 错误模型**
- 采集热路径上超时/CRC 失败/断连是**预期失败**：异常开销大（栈展开、日志）、会打断控制流；Result 让失败成为数据流的一部分。
- `OperationalError`：`Code`（如 `Modbus.Timeout`、`Buffer.QueueFull`）+ `Message` + `Details` + `Severity` + `Category`；工厂方法（Timeout/Communication/Protocol/Validation/Unavailable/NotFound/StorageFull/DatabaseLocked/Storage/General）统一构造口径。
- `OperationResult` / `OperationResult<T>` 提供隐式转换（`OperationalError → Failure`、`T → Success`），调用代码简洁。
- 仍应抛异常：编程错误、配置错误等非预期异常（如空引用、非法枚举转换）；Result 只承载「预期内可恢复」的失败。
- 分类路由：如 `SqliteErrorClassifier` 按 Category/Code 把错误映射到存储行为。

**Q6.3 RawPointValue 边界**
- 职责划分：驱动 = 协议解码（Modbus `ushort[]` → 目标类型 + Endian 处理；OPC UA Variant → .NET 类型）；Pipeline = 工程缩放（ScaleFactor/ScaleOffset）+ 死区 + 组装快照。
- `RawPointValue` 的 `Value` 已解码为领域类型（int/float/double/bool/string）但未缩放；Pipeline 不感知协议细节，只处理数值。

**Q6.4 DriverCapability**
- 解决「采集引擎如何选调用策略」：`SupportsBatchRead/Write`（能否一次请求多点位，不支持则由调用方逐个调 `ReadAsync`）；`SupportsSubscription`（是否支持服务端推送，OPC UA 支持、Modbus 不支持）；`MaxBatchSize`（单次批量上限，0 = 无限制，超限需分批）。
- 参考联动：`ModbusBatchPlanner` / `PointBatchService` 处理分批与地址合并。

**Q6.5 DriverState**
- 四态：Disconnected（初始）→ Connecting → Connected → Faulted（故障需重连）。
- 重连归属：`IProtocolDriverPool` / `ReliableProtocolDriver`（Protocol 模块）负责长连接复用与断线自愈，采集模块只消费状态。

**Q6.6 ConnectionField**
- 意图：元数据驱动前端表单——协议声明「我需要哪些连接参数、长什么样」，前端按 `Type`（text/number/select）、`Options`、`Required` 自动渲染，避免每种协议写一套表单代码；提交值存入 `DeviceConnection.Parameters`。
- 现状：无消费方（预留），可讨论「元数据 vs 每协议专用表单」的取舍：元数据灵活但类型不安全，专用表单强类型但协议多时样板代码膨胀。

**Q6.7 ProtocolIdentifier**
- 值对象理由：替代魔法字符串，相等性语义集中定义（名称 + 方言、忽略大小写），避免散落 `string.Equals(..., OrdinalIgnoreCase)`。
- 细节：`Equals` 用 `OrdinalIgnoreCase` 比较 Name 与 Dialect；`GetHashCode` 用 `ToLowerInvariant()` 组合——关键是与 Equals 同规则。
- 若 Equals 忽略大小写而 GetHashCode 区分大小写：`HashSet`/`Dictionary` 里 `"modbus"` 和 `"Modbus"` 会被放到不同桶，看似等值的对象查不到——值对象最常见的隐藏 bug。
- 预置实例：`Modbus` / `OpcUa` / `S7` / `Unknown`；`ToString()` 返回 `Name/Dialect` 或 `Name`。

---

## 七、校验与不变量

**Q7.1 现状校验**
- `PointManager.ValidateAsync`（应用服务层）：Name 非空、Address 非空、ScanIntervalMs ≥ 0、Deadband ≥ 0；返回 `OperationResult<IReadOnlyList<PointValidationError>>`，一次收集全部错误而非短路。
- `PointValidationError`（Field + Message）好处：错误可精确定位到字段，前端可直接绑定表单校验提示。
- 漏掉：协议级地址格式（`IAddressParser` 未接线）、同一设备内地址重复、DataType 与协议支持性、缩放参数边界（如 ScaleFactor=0 是否允许）。

**Q7.2 校验分层（讨论）**
- 非空/非负：应用服务层（现状正确）。
- 协议地址格式：协议模块（委托 `IAddressParser`，按协议解析 `"40001"` / `"DB1.DBD0"`），Domain 不感知。
- 设备内地址唯一：应用服务 + 仓储（跨实例查询），或 Domain 不变量（仅当 Device 聚合内可判定）。
- 未接线的代价：非法地址到运行时才暴露——采集阶段才失败，用户反馈路径长（配置时不知道错，采集时才知道），且失败信息散落在采集日志而不是配置接口。

**Q7.3 不变量盘点**
- 代码强制：`required`（Name/Address/Protocol/Connection、RawPointValue.Point/Value）；枚举类型本身（DataType/PointAccess/QualityCode/DeviceStatus/DriverState/AggregateKind）不可能出现非法值。
- 注释约定：`ErrorMessage` Good 时为 null（Bad 时不强制填写）；`Deadband` 仅对模拟量有效（Bool/String 不参与，由 Pipeline 行为保证）；`ScanIntervalMs` ≤ 0 继承默认；`Quality` 默认 Good（构造时忘记赋值不会报错）。
- 约定型不变量出错时在哪暴露：越晚越贵——构造时（类型系统）→ 应用校验（接口层，好）→ 运行时行为（采集/告警错乱，难查）。面试加分点：指出「Deadband 注释声称『不触发上报』但 Pipeline 实际『不丢弃数据、只影响告警 Duration 缓存』」（ADR-008 P1-1）。

---

## 八、开放题参考方向

**Q8.1 重构三处（示例方向）**
- 文档漂移：README「不依赖任何项目」与 csproj 引用 Shared 矛盾；`DevicePoint.Deadband` 注释与 Pipeline 语义矛盾（ADR-008）。
- 冗余模型：`Alarm.Events.PointStoredEvent` 死代码；`AggregateKind` / `ConnectionField` 未接线，明确保留或删除。
- 聚合健壮性：`Device` 集合线程安全、AddPoint 重复 ID 校验、RemovePoint 返回值。
- 校验下沉：把「不可变不变量」（如 Deadband ≥ 0）从应用服务移到 Domain 构造校验，让领域对象自证合法。
- 回答无标准答案，考察：是否基于代码事实、是否权衡过代价、能否给出可落地的下一步。

**Q8.2 手写 ProtocolIdentifier**
- 必须包含：Name + Dialect；忽略大小写的 Equals；与 Equals 同规则的 GetHashCode；ToString；预置静态实例。
- 对比点：现有实现用 `record` + 手写 `Equals(ProtocolIdentifier?)`（注意 record 默认相等会被自定义覆盖）；GetHashCode 用 `ToLowerInvariant` 而非 `GetHashCode(StringComparison)` 的写法；Dialect 为 null 时的 HashCode.Combine 行为。

**Q8.3 新增协议（BACnet）**
- Domain 层几乎零改动：`IProtocolDriver` 已抽象连接/读写/批量；`ProtocolIdentifier` 可加预置实例（加法不改动）；`ConnectionField` + `Parameters` 承载协议特有参数；`DataType`/`QualityCode` 复用。
- 实际工作在 Protocol 模块（新驱动实现）+ 配置前端（ConnectionField 元数据渲染）。
- 「接口只增不删」的价值：协议多、演进快，Domain 契约稳定时，新增协议是纯增量，不回归已有 Modbus/S7/OPC UA 实现。

---

## 吃透自检

答完 36 题后，不看代码完成以下三件事：
1. 画出「一轮采集：设备定义 → 驱动解码 → 快照 → 记录 → 事件」的完整数据形态变化，标注每个形态的类名与关键字段。
2. 说出 Domain 里每一个类型：它是实体/值对象/事件/接口/枚举？为什么放在 Domain 而不是其他模块？
3. 指出至少 3 处「注释/文档与实现不一致」或「预留未接线」的点（提示：ADR-008、Alarm.Events、AggregateKind、ConnectionField）。
