# Protocol 模块面试题

> 难度：★ 基础 · ★★ 进阶 · ★★★ 深水。每题附「代码定位」，答不出先看代码再看答案。
> 共 8 组 36 题；参考答案见 `answers.md`。

---

## 一、接口与抽象（IProtocolDriver / 状态 / 能力）

**Q1.1 ★** 列出 `IProtocolDriver` 的全部成员及各自语义。为什么接口定义在 Domain 而不是 Protocol 模块？
代码定位：`src/NitroGateway.Domain/Protocols/IProtocolDriver.cs`。

**Q1.2 ★** `DriverState` 有哪四个状态？各状态由谁转换？设备进入 `Faulted` 后由谁负责恢复？
代码定位：`DriverState.cs`；`ReliableProtocolDriver.ReadBatchAsync` 的自动建连。

**Q1.3 ★** `DriverCapability` 的四个字段分别控制什么？Modbus / S7 / OPC UA 三个驱动的声明值是什么？为什么有差异？
代码定位：`DriverCapability.cs`；`ModbusDriverCapability.cs`；`S7DriverCapability.cs`；`OpcUaDriverCapability.cs`。

**Q1.4 ★★** 接口契约是「所有操作返回 OperationResult、不抛异常」。但代码里仍会抛两类异常，是哪两类？为什么它们被豁免？
代码定位：`ReliableProtocolDriver.ReadBatchAsync` 的 `catch (OperationCanceledException)`；`ModbusAddressParser.Parse`。

**Q1.5 ★★** `PingAsync` 的设计意图是什么？Modbus / S7 / OPC UA 各自用什么样的「最小代价读」？有什么隐患？
代码定位：`ModbusDriverBase.cs` Ping；`S7Driver.cs` Ping；`OpcUaDriver.cs` Ping。

---

## 二、地址解析（PointAddress / IAddressParser）

**Q2.1 ★** `PointAddress` 抽象基类解决什么问题？`Raw` 字段的语义是什么？为什么驱动层不直接收 string？
代码定位：`Abstraction/PointAddress.cs`。

**Q2.2 ★** `IAddressParser` 三个方法的职责？`GetDistance` 返回 -1 / 0 / 正数分别代表什么？为什么 OPC UA 的实现恒返回 -1？
代码定位：`Abstraction/IAddressParser.cs`；`OpcUaAddressParser.GetDistance`。

**Q2.3 ★★** Modbus 地址 `40001` 的解析过程：前缀如何映射功能区？PLC 式地址号如何转 0-based 偏移？边界 1..65536 为什么超限抛异常而不是 `(ushort)` 静默回绕？
代码定位：`ModbusAddressParser.cs` `Parse`（ADR-003 P2-1 注释）。

**Q2.4 ★★** `DataType` 到寄存器数量的映射表是什么？为什么 `String` 特殊？当前 String 固定读多长、定义在哪里？
代码定位：`ModbusAddressParser.DataTypeToRegisterCount`；`ModbusDriverBase.DefaultStringLength`。

**Q2.5 ★★** S7 地址正则支持哪四种 VarType？`DBX` 的 `BitOffset` 在 `S7Driver` 的读路径里被用了吗？存在什么问题？
代码定位：`S7AddressParser.cs`；`S7Driver.ReadAsync` 的地址拼接。

**Q2.6 ★★★** OPC UA 地址为什么设计成强类型而不是裸 string？解析器支持哪四种 NodeId 标识符？
代码定位：`OpcUaAddress.cs`；`OpcUaAddressParser.Parse`。

---

## 三、复合工厂与驱动池（ProtocolDriverFactory / ProtocolDriverPool）

**Q3.1 ★** `ProtocolDriverFactory` 用什么机制注册和创建驱动？遇到未注册的协议会怎样？
代码定位：`Abstraction/ProtocolDriverFactory.cs`；`ProtocolServiceCollectionExtensions.cs`。

**Q3.2 ★★** 为什么 `Create` 每次都要包一层 `ReliableProtocolDriver`？驱动池里缓存的是「包装后的实例」还是「内层驱动」？
代码定位：`ProtocolDriverFactory.Create`；`ProtocolDriverPool.GetOrCreate`。

**Q3.3 ★★** 池的指纹 `BuildKey` 包含哪些字段？为什么 `Parameters` 要排序后再序列化？
代码定位：`ProtocolDriverPool.BuildKey`。

**Q3.4 ★★★** 并发语义：两个线程同时 `GetOrCreate` 同一设备但配置不同，代码如何保证「同一设备只有一个存活驱动」？逐行讲清楚 `GetOrAdd` / `TryUpdate` 的竞争路径。
代码定位：`ProtocolDriverPool.GetOrCreate` 的 while 循环。

**Q3.5 ★★** `Evict` 的作用是什么？什么场景必须由上层调用它？如果设备配置变化但没人调用 Evict，驱动池能自愈吗？
代码定位：`IProtocolDriverPool.cs`；`ProtocolDriverPool.Evict`。

**Q3.6 ★★** 驱动池的单元测试为什么用 Fake 驱动而不是真实驱动？验证了哪几条语义？
代码定位：`tests/NitroGateway.UnitTests/ProtocolDriverPoolTests.cs`。

---

## 四、可靠性与重试（ReliableProtocolDriver）

**Q4.1 ★★** 装饰器只在哪些操作上叠加逻辑？为什么偏偏是 `ReadBatchAsync`，而单点读/写是透传？
代码定位：`ReliableProtocolDriver.cs` 类注释与成员列表。

**Q4.2 ★★** 重试管线的参数是什么（次数/退避/超时）？为什么每次尝试要有独立 3s 超时？
代码定位：`ReliableProtocolDriver` 构造函数的 `ResiliencePipelineBuilder`。

**Q4.3 ★★★** 自动建连逻辑的完整路径：什么条件下触发 `ConnectAsync`？连接失败如何转化为重试？重试全部耗尽后返回什么、日志打什么级别？
代码定位：`ReliableProtocolDriver.ReadBatchAsync` 全文。

**Q4.4 ★★★** 类注释说「Driver 层只打 Debug 日志（单次重试的细节）」，但 `OnRetry` 回调实际打的是什么级别？这是不是文档漂移？你会怎么统一？
代码定位：`ReliableProtocolDriver` 类注释 vs `OnRetry` 的 `LogInformation`。

**Q4.5 ★★★** 为什么「最终的失败 Warning」要留给上层 `DeviceCollector` 而不是驱动自己打？分层意图是什么？
代码定位：`ReliableProtocolDriver` 类注释；`DeviceCollector` 步骤 2。

---

## 五、Modbus 批量读优化（ModbusDriverBase）

**Q5.1 ★★** `ReadBatchAsync` 的五步策略是什么？每一层优化分别解决什么问题？
代码定位：`ModbusDriverBase.ReadBatchAsync` 的策略注释与实现。

**Q5.2 ★★** `MaxMergeGap = 2` 的含义？为什么允许小间隔合并？间隔寄存器被多读后怎么处理？
代码定位：`ModbusDriverBase.cs:20`；`MergeRanges`。

**Q5.3 ★★★** `ModbusBatchPlanner.SplitContiguousSegments` 解决什么问题？为什么「同类型」的点位也要切段？不切会发生什么？
代码定位：`ModbusBatchPlanner.cs`；`ModbusBatchPlannerTests.cs`。

**Q5.4 ★★** 单次请求 125 寄存器上限是哪来的？段内总寄存器数超限时走什么路径？
代码定位：`ModbusDriverBase.MaxRegistersPerRequest`；`ReadRangeAsync` 与 `ReadSegmentFallbackAsync`。

**Q5.5 ★★★** 批量读的失败语义：全部失败 vs 部分失败分别怎么处理？为什么「全部失败置 Faulted」而「部分失败只跳过 + Warning」？
代码定位：`ModbusDriverBase.ReadBatchAsync` 收尾部分（ADR-003 P3-5 注释）。

**Q5.6 ★★** 批量读整段被 try/catch 包住：catch 里做了什么？这与接口「不抛异常」契约如何呼应？
代码定位：`ModbusDriverBase.ReadBatchAsync` 的 catch/finally。

---

## 六、Modbus TCP 与 RTU

**Q6.1 ★** 列出 Modbus TCP 与 RTU 驱动的差异点（客户端、闸门、站号、Endpoint 语义、参数）。
代码定位：`ModbusTcpDriver.cs` vs `ModbusRtuDriver.cs`。

**Q6.2 ★★** 基类为什么把「读写闸门」设计成抽象成员 `ReadGate` + 钩子 `OnGateAcquired`？TCP 与 RTU 的闸门分别是什么？
代码定位：`ModbusDriverBase.cs`；`ModbusRtuDriver.cs` 的 `ReadGate` / `OnGateAcquired`。

**Q6.3 ★★** `SerialPortManager`：同一串口多从站如何共享一个 `ModbusRtu` 句柄？参数不一致会怎样？句柄什么时候关闭？
代码定位：`SerialPortManager.Acquire` / `Release` / `SettingsEqual`。

**Q6.4 ★★** RTU 连接的串口句柄失效（设备拔出）后如何恢复？`ConnectAsync` 里 `IsOpen()` 检查的作用？
代码定位：`ModbusRtuDriver.ConnectAsync`。

**Q6.5 ★★** 连接失败如何分类为 `Timeout` / `Communication`？实现上为什么靠消息字符串匹配？有什么脆弱性？
代码定位：`ModbusTcpDriver.ClassifyConnectError`。

**Q6.6 ★★** 字节序：Modbus 标准是什么？HslCommunication 默认是什么？未配置时驱动显式采用哪个？
代码定位：`ModbusDriverBase.ParseDataFormat`。

**Q6.7 ★★** `UnitId` 为什么 clamp 到 1..247？Endpoint 的端口解析规则是什么（默认端口）？
代码定位：`ModbusTcpDriver` 构造函数与 `ConnectAsync`。

---

## 七、S7 与 OPC UA

**Q7.1 ★★** S7 驱动连接需要哪些参数？各自的默认值？CpuType 支持哪几种？
代码定位：`S7Driver.ConnectAsync`。

**Q7.2 ★★★** S7 的 `ReadBatchAsync` 是「逐点循环」还是「协议级批量」？`Capability.SupportsBatchRead = true` 与实现是否一致？怎么改进？
代码定位：`S7Driver.ReadBatchAsync` vs `S7DriverCapability.cs`。

**Q7.3 ★★★** S7 写操作统一 `Convert.ToSingle(value)` 有什么问题？Bool / Int64 / String 点位写入会怎样？修复方向是什么？
代码定位：`S7Driver.WriteAsync`（对比 `ModbusTcpDriver.WriteSingleValueAsync` 的 ADR-003 P1-2 全量映射）。

**Q7.4 ★★** OPC UA 驱动当前状态：`ConnectAsync` 抛 `NotImplementedException`（OpcUa 未入 slnx）。除了调通 SDK，代码里还有什么生产不可接受的点？
代码定位：`OpcUaDriver.cs:53` 与 `AutoAcceptUntrustedCertificates`。

**Q7.5 ★★★** `IBrowseableDriver` 为什么独立于 `IProtocolDriver`？两者调用方分别是谁？
代码定位：`OpcUa/IBrowseableDriver.cs` 类注释。

**Q7.6 ★★** OPC UA 的 `ReadBatchAsync` 如何组织一次多节点读？点位时间戳用谁的、什么情况下回退？
代码定位：`OpcUaDriver.ReadBatchAsync`（ReadValueIdCollection / SourceTimestamp）。

---

## 八、测试、找茬与开放题

**Q8.1 ★★** 协议模块相关的测试有哪些？各自覆盖什么行为？
代码定位：`ProtocolDriverPoolTests` / `ModbusAddressParserTests` / `ModbusBatchPlannerTests` / `ModbusTcpDriverIntegrationTests`。

**Q8.2 ★★★** 找茬：不看答案，从代码里找出至少 4 个「已知简化或缺陷」（提示：S7 位地址、S7 写类型、Ping 硬编码、日志级别漂移、OPC UA 证书）。各给出修复方向。

**Q8.3 ★★★** 假设要新增一个欧姆龙 PLC 协议，最小接入清单是什么？哪些文件必须动、哪些不能动（Abstraction 接口只增不删）？
代码定位：`ModbusRegistration` / `ProtocolServiceCollectionExtensions`；AGENTS.md 雷区。

**Q8.4 ★★★** 一台设备 1000 个点位、1s 采集周期：结合现有代码说明如何保证不压垮 PLC？最坏情况下单段批量读的耗时上界是多少？上层如何兜底？
代码定位：`ModbusDriverBase` 批量读 + `ReliableProtocolDriver` 重试参数 + Collection 熔断。

**Q8.5 ★★★** 设备运行中被修改了 IP：驱动池如何感知并切换？指纹里漏掉什么字段会导致「改了配置却复用了旧驱动」？你会怎么预防？
代码定位：`ProtocolDriverPool.BuildKey` 与 `GetOrCreate` 快速路径。
