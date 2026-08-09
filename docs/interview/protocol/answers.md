# Protocol 模块面试题 · 参考答案

> 要点 + 代码定位 + 相关测试。先自己答，再对照；答不上来回到代码里把答案「读出来」再背一遍。
> 代码是唯一事实来源：部分注释与实际实现不一致（Q4.4 即漂移题），答题时以代码行为为准。

---

## 一、接口与抽象

**Q1.1 IProtocolDriver 成员与放置位置**
成员：`State`（连接状态）、`Capability`（能力声明）、`ConnectAsync` / `DisconnectAsync`（建连/断开）、`PingAsync`（最小代价连通性验证）、`ReadAsync` / `ReadBatchAsync`（单点/批量读）、`WriteAsync` / `WriteBatchAsync`（单点/批量写），继承 `IDisposable`。全部返回 `OperationResult`，契约注明「不抛异常」（`IProtocolDriver.cs:15`）。
放在 Domain：上层（Collection / Device）只依赖接口编程，不依赖具体协议实现 → 依赖倒置；Domain 不引用基础设施，Protocol 反向依赖 Domain（`ProtocolIdentifier` / `DevicePoint` / `DataType`）。

**Q1.2 DriverState 四态与恢复**
`Disconnected`（初始）→ `Connecting`（连接中）→ `Connected`（成功）→ `Faulted`（故障）。转换都在驱动内部：如 `ModbusTcpDriver.ConnectAsync` 成功置 Connected、失败置 Faulted；`DisconnectAsync` 回 Disconnected。`Faulted` 后由 `ReliableProtocolDriver.ReadBatchAsync` 自动恢复：`State != Connected` → 调 `ConnectAsync`，成功即继续读取（`ReliableProtocolDriver.cs:100`）。另外 Modbus 批量读「全部点位失败」会主动置 `Faulted` 让重试管线重新建连（`ModbusDriverBase.cs:215`）。

**Q1.3 DriverCapability 对比**
字段：`SupportsBatchRead` / `SupportsBatchWrite`（采集引擎决定是否批量调用）、`SupportsSubscription`（服务端主动推送，Modbus 不支持）、`MaxBatchSize`（单次批量上限，0=无限制）。
- Modbus：批量读/写 true，订阅 false，125（协议 03/04 功能码上限）
- S7：批量读/写 true，订阅 false，20
- OPC UA：批量读/写 true，订阅 true，0 无限制
差异根源：协议能力不同（OPC UA 有订阅模型；Modbus 是请求-响应轮询）。

**Q1.4 仍会抛的两类异常**
- `OperationCanceledException`：取消不是「失败」，必须向上传播终止任务（`ReliableProtocolDriver` 单独 catch 并 rethrow，不让 Polly/兜底 catch 吞掉）
- `ArgumentException`：配置/地址错误要快速失败暴露（`ModbusAddressParser.Parse` 对空地址、非法前缀、超范围直接抛），而不是运行时反复重试掩盖配置错误

**Q1.5 Ping 的最小代价读与隐患**
- Modbus：`ReadInt16("0")`（读 40001 一个寄存器，`ModbusDriverBase.cs:107`）
- S7：`ReadInt16("DB1.DBW0")`（`S7Driver.cs:76`）
- OPC UA：读 `VariableIds.Server_ServerStatus` 内置节点（`OpcUaDriver.cs:70`）
隐患：地址是**硬编码**的——设备若没有 40001 或 DB1，Ping 会误报失败（假阴性）；S7 的 DB1 在很多 CPU 上不存在。改进方向：用协议级无业务含义的探测（如 Modbus 功能码 08 诊断、S7 通信测试），或把 Ping 地址做成可配置。

---

## 二、地址解析

**Q2.1 PointAddress 的作用**
协议无关的地址容器：驱动层收 `PointAddress` 子类，可做强类型校验与距离计算；`Raw` 保留用户原始字符串，供序列化、日志、UI 回显。直接传 string 的话每个驱动都要重复解析和校验，且无法表达「两个地址可否合并」。

**Q2.2 IAddressParser 三方法**
- `Parse`：原始字符串 → 协议特化 `PointAddress`（非法输入抛 `ArgumentException`）
- `Serialize`：地址对象 → 原始字符串（回写 UI / 配置）
- `GetDistance`：批量优化用——返回 -1 表示不可比（不同类型/不同功能区），0 表示紧邻可合并，正数表示间隔寄存器数。OPC UA 恒返回 -1：OPC UA 节点没有「连续地址」概念（`OpcUaAddressParser.cs:73`），不能像 Modbus 那样合并读取。

**Q2.3 Modbus 地址解析**
首字符 `0/1/3/4` → `Coil / DiscreteInput / InputRegister / HoldingRegister`，其余抛异常；剩余部分按 PLC 式地址号解析，`offset = 地址号 - 1`（40001 → HoldingRegister offset 0）；范围 1..65536，超限抛 `ArgumentException`（`ModbusAddressParser.cs:30`）。ADR-003 P2-1：之前 `(ushort)` 强转会静默回绕（40000 → 0），读到错误寄存器极难排查，所以改为显式抛异常让配置错误暴露。

**Q2.4 数据类型 → 寄存器数**
Bool/Byte/Int16/UInt16 = 1；Int32/UInt32/Float = 2；Int64/UInt64/Double = 4；String 最少 1、实际按长度算（`ModbusAddressParser.cs:88`）。String 特殊：长度可变，寄存器数依赖内容。当前 v1 固定读 10 字符（`DefaultStringLength = 10`，`ModbusDriverBase.cs:37`，ADR-003 P2-2 协议约定）；点位级 StringLength 需要改抽象读签名才能支持，已留注释说明。

**Q2.5 S7 地址与 BitOffset 缺陷**
正则 `^DB(\d+)\.DB([BDWX])(\d+)(?:\.(\d+))?$` 支持 DBD（DWord）/ DBW（Word）/ DBB（Byte）/ DBX（Bit）四种（`S7AddressParser.cs:8`），DBX 的 `.bit` 解析进 `BitOffset`。
缺陷：`S7Driver.ReadAsync` 拼接地址时只拼 `DB{n}.{VarType}{ByteOffset}`，**丢弃 BitOffset**（`S7Driver.cs:52-55`）——`DB1.DBX0.1` 会被当成 `DB1.DBX0` 读，位地址点位读取结果错误。修复：拼接时对 DBX 带上 `.{BitOffset}`；这是已确认的已知简化点（答案里的找茬题素材）。

**Q2.6 OPC UA 强类型地址**
OPC UA NodeId 规范有四类标识符，解析器全部支持：`i=` 数字、`s=` 字符串、`g=` GUID、`b=` Opaque 字节（base64）（`OpcUaAddressParser.cs:22-62`），统一为 `OpcUaAddress`（NamespaceIndex + 四种 Id 之一）。强类型原因：裸 string 解析有歧义、无法编译期保证，强类型可直接映射 SDK 的 `NodeId` 构造。

---

## 三、复合工厂与驱动池

**Q3.1 复合工厂机制**
`ProtocolDriverFactory` 持有 `Dictionary<string, Func<...>>`（大小写不敏感），各协议模块通过 `Register(protocolName, factory)` 注册自己的驱动构造器；`Create` 按 `ProtocolIdentifier.Name` 查表，找不到抛 `NotSupportedException`（`ProtocolDriverFactory.cs:38`）。注册时机：`AddNitroProtocol` 注册单例工厂，首次解析时执行 `ModbusRegistration.Register` + `S7Registration.Register`（`ProtocolServiceCollectionExtensions.cs`）。

**Q3.2 为什么包一层装饰器**
工厂 `Create` 总是返回 `new ReliableProtocolDriver(inner, logger)`——把「自动建连 + 重试」从各驱动里抽出来横切，具体驱动（Modbus/S7）只关心协议收发。池缓存的是**包装后**的实例（`GetOrCreate` 直接返回 `_factory.Create` 的结果），所以重试能力对池内所有驱动统一生效。

**Q3.3 指纹 BuildKey**
包含：协议名、方言（Dialect）、Endpoint、连接超时、请求超时、重试次数、重试间隔、协议参数 JSON（`ProtocolDriverPool.cs:74`）。参数先按 key 排序再 `JsonSerializer.Serialize`：`Dictionary` 枚举顺序不确定，不排序会导致「同样配置两次算出不同指纹」→ 误重建驱动、白白断开长连接。

**Q3.4 并发唯一存活（重点题）**
快速路径：缓存命中且指纹相同 → 直接复用。
竞争路径（`ProtocolDriverPool.cs:36-55`）：两个线程各自 `_factory.Create` 新驱动 → `GetOrAdd` 只有一个能成为字典值（winner），另一个拿到已有 entry：
- winner 指纹相同 → 复用 winner 的驱动，`Dispose` 自己刚建的（try/catch 吞掉释放异常）
- winner 指纹不同（旧配置）→ `TryUpdate(device.Id, entry, winner)` CAS：成功则替换并 `Dispose` 旧驱动；失败说明被别的线程抢先更新，回到循环重试
结论：任何时刻字典里每设备只有一条 entry、一个存活驱动；失败方负责释放自己的新建实例，不泄漏。

**Q3.5 Evict 语义**
`Evict(deviceId)`：从池移除并释放该设备驱动（`ProtocolDriverPool.cs:65`）。必须由上层调用：设备**删除**、设备**下线**、设备状态变更后要主动释放长连接（接口注释：设备更新/删除/状态变更由上层调用 Evict）。「配置变化但没人 Evict」能自愈：下次 `GetOrCreate` 指纹不匹配会走重建路径（自动替换 + 释放旧驱动）；Evict 主要解决「不再使用的设备连接滞留」。

**Q3.6 池的测试**
用 `FakeDriverFactory`（计数 CreatedCount）+ `FakeDriver`（跟踪 Disposed），不依赖真实 PLC，确定性验证三条语义（`ProtocolDriverPoolTests.cs:10`）：
1. 同参数 → 返回同一实例、只创建 1 次、不 Dispose
2. Endpoint/参数变化 → 重建并 Dispose 旧驱动
3. `Evict` → Dispose 且下次调用重建；`Dispose` → 全部驱逐
共 5 个用例。这是「接口 + Fake」的经典测试手法，也是 Q8.1 的素材。

---

## 四、可靠性与重试

**Q4.1 为什么只装饰批量读**
叠加逻辑只出现在 `ReadBatchAsync`：自动建连 + Polly 重试 + 超时。原因（类注释 `ReliableProtocolDriver.cs:13`）：批量读是采集主路径（1s 一轮、上百点位），最容易受网络抖动影响；**写操作重试可能重复写**（非幂等），单点读/写/连/断由上层或调用方控制节奏，透传内层。

**Q4.2 重试参数**
`MaxRetryAttempts = 3`，首次延迟 500ms，指数退避 500ms → 1s → 2s（`DelayBackoffType.Exponential`），每次尝试独立 `AddTimeout(3s)`（`ReliableProtocolDriver.cs:44-60`）。独立超时：单次尝试卡死不占用整个重试序列的预算，每次尝试都有确定上界；总最坏耗时 ≈ 3s×4 + 3.5s 退避 ≈ 15.5s（只读路径）。

**Q4.3 自动建连与最终失败**
管线内：`State != Connected` → `ConnectAsync`，连接失败 `throw` → 触发 Polly 重试；`ReadBatchAsync` 返回失败也 `throw` → 重试。重试耗尽 → catch → 返回 `OperationResult.Failure(OperationalError.Protocol(...))`，打 **Debug** 日志（不重复 Warning，注释：上层 DeviceCollector 会打最终 Warning）。`OperationCanceledException` 单独 rethrow，不转失败（取消不是故障）。

**Q4.4 日志级别漂移（找茬题）**
类注释写「Driver 层只打 Debug 日志（单次重试的细节）」，但 `OnRetry` 回调实际 `LogInformation`（`ReliableProtocolDriver.cs:61`），最终失败才 Debug——注释与实现不一致。影响：每次重试都打 Information，高频抖动时日志量不小。统一方向二选一：按注释把 OnRetry 降为 Debug；或注释更新为「重试细节 Information、最终失败 Debug」。

**Q4.5 日志分层意图**
驱动只有协议名 logger，没有设备名/点位名等业务上下文；`DeviceCollector` 持有设备，打 Warning 时能带上设备标识，一条日志即可定位。分层避免「驱动刷屏 + 上层重复」，诊断细节留在 Debug 供按需开启。

---

## 五、Modbus 批量读优化

**Q5.1 五步策略**
1. 解析每个点位地址 + DataType → 寄存器数（`ParseWithCount`）
2. 按功能区（Area）分组（不同功能码不能合并）
3. 组内按 offset 排序
4. 贪心合并：间隙 ≤ 2 寄存器的点位并入同一 Range
5. Range 内按 DataType 再分组 → 连续段切分 → 每段 ≤ 125 寄存器 → HSL 批量读（`ModbusDriverBase.cs:175-234`）
每层解决：功能码差异、乱序输入、报文数量、协议单次上限、类型宽度差异。

**Q5.2 MaxMergeGap=2**
间隙 ≤ 2 个寄存器也合并成一次读，多读的间隔寄存器直接丢弃——用少量带宽换一次请求，减少 RTT。间隙 > 2 就关闭当前 Range 开新 Range（`ModbusDriverBase.cs:289`）。2 是经验值：间隙小的寄存器通常是被其他点位/类型占用的，一次连读仍划算。

**Q5.3 SplitContiguousSegments**
同一 DataType 的点位之间可能被其他类型点位或空寄存器隔开（如 Float@40001、Int16@40003、Float@40004）。从段首连读会把间隔寄存器当成后序点位的值——**错位**。所以按「offset 严格等于前一点 offset + 前一点寄存器数」切连续段，每段独立批量读（`ModbusBatchPlanner.cs`）。测试：`ModbusBatchPlannerTests`（Float 带 gap 切 2 段、混合类型切段）与 `ModbusTcpDriverIntegrationTests.ReadBatchAsync_NonContiguousSameType_SplitsSegments`（真实 TCP 回环验证 A/B/C/D 布局）。

**Q5.4 125 上限与回退**
Modbus 协议功能码 03/04 单次最多 125 寄存器（协议限制，`ModbusDriverBase.MaxRegistersPerRequest`）。段总寄存器数 > 125 → 不走批量，改 `ReadSegmentFallbackAsync` 逐点读（`ModbusDriverBase.cs:338`）。注意：上限按「段」算，`Capability.MaxBatchSize = 125` 是点位数量声明，与寄存器数不是一个维度。

**Q5.5 失败语义（ADR-003 P3-5）**
- 全部点位无数据 → `State = Faulted` + 返回 Protocol 错误：全挂说明链路/设备级故障，置 Faulted 让重试管线重新建连
- 部分失败 → 跳过失败点、成功点照常返回，`LogWarning("{Ok}/{Total}")`：个别点位问题不值得整批重试，重试只会放大无效流量
「部分失败不算链路故障」是这套语义的核心：熔断/重连只认链路级失败。

**Q5.6 异常收口**
整段 try/catch：catch → `State = Faulted` + `OperationalError.Protocol("批量读取失败: ...")`；finally 释放闸门（`ModbusDriverBase.cs:225-233`）。呼应接口契约「不抛异常」：所有通信异常在驱动边界转成 `OperationResult`，上层统一按结果分支，不需要 try/catch 调用方。

---

## 六、Modbus TCP 与 RTU

**Q6.1 差异清单**
- 客户端：`ModbusTcpNet`（TCP）vs `ModbusRtu` + `System.IO.Ports`（串口）
- 闸门：TCP 是驱动内 `SemaphoreSlim`；RTU 是 `SerialPortManager` 的共享闸门（同端口多从站共用）
- 站号：TCP 构造时固定 `Station`；RTU 每次通信前 `OnGateAcquired` 切换到本从站
- Endpoint 语义：`ip:port` vs `COM3`（端口名）
- 参数：TCP 用 UnitId/DataFormat；RTU 额外 BaudRate/DataBits/Parity/StopBits，超时透传 `RequestTimeoutMs`（ADR-003 P3-4）

**Q6.2 闸门抽象**
基类把闸门做成抽象 `ReadGate` + 钩子 `OnGateAcquired`（`ModbusDriverBase.cs:49-56`）：基类的单点读写/批量读都在「Wait 闸门 → OnGateAcquired → 操作 → Release」包裹内执行，但锁从哪来、要不要切站号由子类决定。TCP 锁在驱动内（`_readLock`）；RTU 在**连接后**用共享 `_lease.Gate`（同端口所有从站同一把锁 → 帧级串行），未连接时退化为驱动内锁 `_sync` 兜底。

**Q6.3 串口共享**
`SerialPortManager` 以端口名为 key：首次打开创建 `ModbusRtu` + `SemaphoreSlim` + LeaseCount=1；后续同端口 Acquire 校验参数（BaudRate/DataBits/Parity/StopBits/DataFormat/超时，`SettingsEqual`）一致则 LeaseCount++，不一致抛 `InvalidOperationException`（拒绝静默覆盖配置）；Release 减计数，归零才 `Close` + `Dispose`（`SerialPortManager.cs:106-175`）。`GetStatus` 暴露端口占用/租约数供 UI 排障。

**Q6.4 串口失效恢复**
`ConnectAsync`：已 Connected 且 `Rtu.IsOpen()` → 直接复用；否则释放旧租约、重新 `Acquire`（重开会话句柄）（`ModbusRtuDriver.cs:53-77`）。设备拔出/串口异常后句柄失效，不加检查会一直用坏句柄。

**Q6.5 错误分类**
`ClassifyConnectError`：消息含「超时/timeout」→ `OperationalError.Timeout`，否则 `Communication`（`ModbusTcpDriver.cs:78-88`）。靠字符串匹配是因为 HSL 未提供结构化错误码；脆弱点：错误消息文案随库版本变化可能漏判，可讨论改成按异常类型/额外参数分类。

**Q6.6 字节序**
Modbus 标准是多寄存器**高字在前（ABCD）**；HslCommunication 默认 CDAB。驱动未配置时显式 `DataFormat.ABCD`，避免「以为标准实际被 Hsl 默认值带偏」；可配置 CDAB/BADC/DCBA（`ModbusDriverBase.cs:86-92`）。这是踩坑经验写成的默认值。

**Q6.7 UnitId 与端口**
`UnitId` 从参数读取，`Math.Clamp(1, 247)` 收敛到协议范围（0 是广播、248-255 保留）（`ModbusTcpDriver.cs:27`）。端口：默认 502（Modbus 标准），`ConnectAsync` 解析 Endpoint 的 `ip:port`，合法端口覆盖默认值（`ModbusTcpDriver.cs:46-52`）。

---

## 七、S7 与 OPC UA

**Q7.1 S7 连接参数**
Endpoint `ip:port`（默认端口 102）；Rack 默认 0、Slot 默认 1；CpuType 支持 S71200（默认）/S1500/S300/S400，映射 `SiemensPLCS` 枚举（`S7Driver.cs:25-40`）。注意 rack/slot 是 S7 寻址的核心，配错连不上。

**Q7.2 S7 批量读名不副实**
`ReadBatchAsync` 是 `foreach → ReadAsync` 逐点循环（`S7Driver.cs:102-107`），**不是协议级批量**，但 `SupportsBatchRead = true`（`S7DriverCapability.cs`）。上层以为省了请求数，实际还是 N 次单点读。改进：用 HSL 的批量读 API（如 `ReadAsync(string[] addresses)` / `SiemensS7Net` 的组读）或按 DB 块合并；实现前应先把 Capability 改成 false 或同步实现真批量，避免声明与行为不一致。

**Q7.3 S7 写类型缺陷**
`WriteAsync` 统一 `_client.Write(addr, Convert.ToSingle(value))`（`S7Driver.cs:109-115`）：Bool 会变 0/1 float（写进 Word 地址），Int64/Double/String 直接丢精度或失败。修复方向：对照 `ModbusTcpDriver.WriteSingleValueAsync` 的 ADR-003 P1-2「按 DataType 全量映射 HSL 写方法」重写（Bool→Write bit、Int16/32/64→对应重载、String→Write string），并补写读回环测试。

**Q7.4 OPC UA 现状**
`ConnectAsync` 抛 `NotImplementedException`（`OpcUaDriver.cs:53`），且 `AutoAcceptUntrustedCertificates = true` + 空证书校验 handler（`OpcUaDriver.cs:47-49`）——生产不可接受（中间人风险）。OpcUa 未入 slnx（AGENTS.md 雷区：确认前不启用不删除）。推进：对 Prosys/UA Expert 实测 SDK 1.5 API、补 Endpoint 选择与证书信任策略、写真实服务器集成测试。

**Q7.5 IBrowseableDriver 独立**
Browse（浏览节点树）是**配置/导入工具**的能力，采集引擎不调用；独立接口避免把「运维辅助能力」塞进采集热路径接口，也保持 `IProtocolDriver` 稳定（接口只增不删）。`BrowseNode` 返回 NodeId/Name/TypeName/IsVariable/Access，供 UI 选点。

**Q7.6 OPC UA 批量读**
构造 `ReadValueIdCollection`（每点 NodeId + `Attributes.Value`）→ 一次 `Session.ReadAsync` 多节点（`OpcUaDriver.cs:92-110`）→ 按序映射结果。时间戳：优先用服务器 `SourceTimestamp`，为 `DateTime.MinValue`（服务器没带）时回退 `DateTime.UtcNow`（`OpcUaDriver.cs:117-119`）。

---

## 八、测试、找茬与开放题

**Q8.1 测试地图**
- `ProtocolDriverPoolTests`（5 用例）：池的复用/重建/驱逐/并发释放
- `ModbusAddressParserTests`：四功能区映射、0-based 偏移、超限抛异常、`ParseWithCount` 寄存器数
- `ModbusBatchPlannerTests`：空/连续/带 gap/混合切段
- `ModbusTcpDriverIntegrationTests`：`ModbusTcpServer` 真实回环——非连续同类型点位切段读值正确 + 各 DataType 写读回环（ADR-003 P1-1/P1-2 的验收测试）
缺口观察（加分项）：无 RTU 串口测试、无重试装饰器单测、无 S7 测试——可问候选人怎么补。

**Q8.2 已知简化/缺陷清单（至少答出 4 个）**
1. S7 位地址：`DBX0.1` 的 BitOffset 被丢弃（`S7Driver.cs:52-55`）→ 拼接时带上 `.bit`
2. S7 写类型：统一 `Convert.ToSingle`（`S7Driver.cs:109`）→ 按 DataType 全量映射（参考 ADR-003 P1-2）
3. Ping 硬编码：Modbus 读 "0"、S7 读 "DB1.DBW0" → 设备无该地址会假阴性；改为可配置或协议级诊断
4. 日志漂移：注释「只打 Debug」vs OnRetry 实际 Information（`ReliableProtocolDriver.cs:61`）
5. OPC UA 证书：`AutoAcceptUntrustedCertificates = true` + Connect 未实现（`OpcUaDriver.cs:47-53`）
6. S7 批量读声明与实现不一致（`S7DriverCapability.cs` vs `ReadBatchAsync`）

**Q8.3 新增协议接入清单**
必须动：新建协议目录（如 `Protocol.Omron`）——`IProtocolDriver` 实现、地址解析器（继承 `PointAddress` + 实现 `IAddressParser`）、`DriverCapability`、`ServiceCollectionExtensions`（注册依赖）、`Registration`（`factory.Register(name, ...)`，参考 `ModbusRegistration`）；在 `AddNitroProtocol` 中调用注册。不能动：`Abstraction/` 与 `Domain/Protocols` 接口只增不删；slnx 未收录的模块（Mitsubishi/OpcUa）在确认前不启用不删除；不升级第三方依赖（雷区）。上层（Collection/Forwarder）零改动。

**Q8.4 1000 点位 1s 周期**
- 批量合并：按 Area/类型/连续段合并，把 N 次请求压到几十次；gap ≤ 2 容忍；每段 ≤ 125 寄存器
- 失败降级：段失败回退逐点（少读不重读）；部分失败跳过不整批重试
- 重试与超时：3 次指数退避 + 每尝试 3s——最坏单段耗时 ≈ 3s×4 + 3.5s ≈ 15.5s（远超 1s 周期，说明协议层只能兜底，不能依赖它保证周期）
- 上层兜底：Collection 熔断器（链路级）、健康监控（设备级）、轮内并发限流；真正超时设备应尽快 Faulted 让熔断器打开，而不是每轮硬读
加分：指出 `MaxBatchSize=125` 是「点位数量」上限，分段按寄存器数而非点数；讨论是否该把并发/分片做进驱动。

**Q8.5 运行中改 IP**
池自愈路径：`GetOrCreate` 快速路径比对指纹 → Endpoint 变化 → 重建 + 释放旧驱动（`ProtocolDriverPool.cs:24-55`）。但仍需 Device 模块在设备更新后调 `Evict`：删除/下线场景没有下一次 GetOrCreate，只能靠 Evict 释放连接。
指纹隐患：`BuildKey` 靠「参数排序后 JSON 序列化」——若新增连接字段（如超时类）忘记加入指纹、或参数值是对象且 JSON 序列化不稳定（如 JsonElement 包装差异），会出现「配置变了但指纹没变 → 复用旧连接」。预防：指纹构造集中单点 + 单测锁定指纹内容（对比 `ProtocolDriverPoolTests` 的配置变化用例，可扩展断言 BuildKey 敏感字段清单）。
