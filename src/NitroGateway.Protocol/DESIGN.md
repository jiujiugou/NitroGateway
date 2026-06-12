# Protocol 模块设计文档

## 定位

协议适配层。定义地址解析、驱动工厂的抽象接口，并实现各协议驱动。
Protocol 只负责"和设备说话"——收发字节、按协议规则解帧。**不做工程值转换**（缩放、死区），那是 Collection 的事。

---

## 项目结构

```
NitroGateway.Protocol.Abstractions/          ← 零外部依赖
├── IAddressParser.cs                        地址解析接口
├── PointAddress.cs                          抽象基类 + 协议特化子类
├── RawPointValue.cs                         驱动返回的原始值
└── IProtocolDriverFactory.cs                驱动工厂接口

NitroGateway.Protocol.Modbus/                ← 引用 Abstractions + Domain + FluentModbus
├── ModbusAddressParser.cs
├── ModbusAddress.cs                         PointAddress 子类
├── ModbusDriver.cs                          IProtocolDriver 实现
├── ModbusEndian.cs                          字节序
├── ModbusServiceCollectionExtensions.cs     DI
└── ModbusDriverCapability.cs                能力声明

NitroGateway.Protocol.OpcUa/                 ← 后续
NitroGateway.Protocol.S7/                    ← 后续
```

---

## 职责边界（关键）

```
Protocol 负责                           Collection 负责
───────────────────────────            ──────────────────────────
连接/断开                               采集调度
发送 Modbus 帧、OPC UA Read            点位分组（调 IAddressParser.GetDistance）
接收字节、校验 CRC、解帧               类型转换（byte[] → int/float/string）
返回 RawPointValue                     缩放（ScaleFactor + ScaleOffset）
                                        死区判定
                                        生成 PointSnapshot
```

**为什么这么切：**

1. 如果 Protocol 直接返回 PointSnapshot，驱动需要知道 ScaleFactor——这是应用层知识，不是协议知识。OPC UA 设备读到的 Int16 值代表什么，驱动不应该关心。

2. 即使不缩放，驱动做类型转换（byte[] → float）也已经跨过了协议边界——Endian 选择是硬件层的，但"这 4 个字节是 Float 还是 Int32"是应用配置（DevicePoint.DataType）决定的。

3. 在方案 1 中两者都做了类型转换，职责重叠。修正后 Collection 是唯一的数据处理入口，Pipeline 是纯函数，方便测试。

---

## Abstractions 层

### PointAddress — 抽象基类 + 协议特化子类（不用 Dictionary）

```csharp
/// <summary>协议无关的地址抽象</summary>
public abstract record PointAddress
{
    public required string Raw { get; init; }
}

/// <summary>Modbus 功能区枚举</summary>
public enum ModbusArea { Coil, DiscreteInput, InputRegister, HoldingRegister }

/// <summary>Modbus 地址</summary>
public sealed record ModbusAddress(
    ModbusArea Area,
    ushort Offset,
    ushort Count)
    : PointAddress;

/// <summary>S7 地址</summary>
public sealed record S7Address(
    int Db,
    int Offset,
    int Size)
    : PointAddress;

/// <summary>OPC UA 地址</summary>
public sealed record OpcUaAddress(
    ushort Namespace,
    string Identifier)
    : PointAddress;
```

`PointAddress` 放 Abstractions 层，协议特化子类和 `IAddressParser` 实现一起放在各自的协议项目里。调用方用 `switch(address is ModbusAddress m)` 做模式匹配，编译期保证类型安全，不会有 `(int)dict["Offset"]` 炸掉。

### RawPointValue — 驱动输出的原始值

```csharp
/// <summary>
/// 驱动返回的原始数据，未经类型转换和缩放。
/// 由 Collection.PointValuePipeline 消费。
/// </summary>
public sealed class RawPointValue
{
    /// <summary>对应的点位定义</summary>
    public required DevicePoint Point { get; init; }

    /// <summary>
    /// 协议返回的原始数据，类型由各协议自行约定：
    /// Modbus → ushort[]（寄存器值，未做 Endian 处理）
    /// OPC UA → Variant
    /// S7    → byte[]
    /// Pipeline 按协议做一次 dispatch 解析
    /// </summary>
    public required object RawData { get; init; }

    /// <summary>数据源时间戳</summary>
    public DateTime Timestamp { get; init; }
}
```

### IAddressParser

```csharp
/// <summary>协议地址解析器。每种协议提供一个实现</summary>
public interface IAddressParser
{
    /// <summary>解析原始地址字符串 → 协议特化 PointAddress 子类</summary>
    PointAddress Parse(string rawAddress);

    /// <summary>序列化回原始地址字符串</summary>
    string Serialize(PointAddress address);

    /// <summary>
    /// 计算两个地址的距离，用于批量优化判断是否可合并。
    /// 返回 -1 表示不可比（不同类型或不同功能区）。
    /// 返回 0 表示紧邻，可合并为一次批量读。
    /// </summary>
    int GetDistance(PointAddress a, PointAddress b);
}
```

### IProtocolDriverFactory

```csharp
/// <summary>根据协议标识和连接参数创建驱动实例</summary>
public interface IProtocolDriverFactory
{
    IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection);
}
```

---

## IProtocolDriver 补充

已有接口需加 `PingAsync`：

```csharp
/// <summary>连接验证，发最小代价的读请求确认设备可达</summary>
Task<OperationResult> PingAsync(CancellationToken ct = default);
```

完整读写签名（修正后）：

```csharp
/// <summary>读单个点位</summary>
Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct);

/// <summary>批量读取。返回原始值列表，个别失败的点位不包含在内</summary>
Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
    IEnumerable<DevicePoint> points, CancellationToken ct);

/// <summary>写单个点位</summary>
Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct);
```

**返回值从 PointSnapshot 改为 RawPointValue**，驱动不再做类型转换。

### 采集链路（修正后）

```
CollectionEngine
    │
    ├── PointBatchOptimizer.Optimize(points)
    │       → 3 个 PointBatch（HoldingRegister 连续段 × 2、InputRegister × 1）
    │
    ├── ModbusDriver.ReadBatchAsync(batch1)
    │       → FC3 读 40001-40010 → 20 字节
    │       → 按点位拆分 byte[]，拼入 RawPointValue
    │       → 返回 List<RawPointValue>
    │
    ├── PointValuePipeline.Process(batch1RawValues)
    │       → 类型转换（byte[] → DataType）
    │       → 缩放（× ScaleFactor + ScaleOffset）
    │       → 死区
    │       → List<PointSnapshot>
    │
    └── Storage / Forwarder / DeviceHealthMonitor
```

---

## Modbus 实现

### ModbusAddressParser

```
"40001" → ModbusAddress(Area: HoldingRegister, Offset: 0, Count: 2)
"30001" → ModbusAddress(Area: InputRegister,  Offset: 0, Count: 1)
"00001" → ModbusAddress(Area: Coil,            Offset: 0, Count: 1)
```

地址格式约定：
- `0xxxx` = Coil
- `1xxxx` = Discrete Input
- `3xxxx` = Input Register
- `4xxxx` = Holding Register
- `xxxxx - 1` = Offset（PLC 式，ZeroBased）

`Count` 由 `DevicePoint.DataType` 决定：

| DataType | 寄存器数 |
|---|---|
| Bool / Byte / Int16 / UInt16 | 1 |
| Int32 / UInt32 / Float | 2 |
| Int64 / UInt64 / Double | 4 |
| String | 按字节长度 ÷ 2 |

### ModbusDriver

```csharp
public sealed class ModbusDriver : IProtocolDriver
{
    // 连接
    ConnectAsync()    → FluentModbus.Connect()
    DisconnectAsync() → FluentModbus.Disconnect()
    PingAsync()       → 尝试读一个已知的 HoldingRegister，成功则返回 Success

    // 读 → 返回 RawPointValue
    ReadAsync(point)
        → 调 ReadBatchAsync 的单点版本

    ReadBatchAsync(points)
        → 按 Area 分组（HoldingRegister / InputRegister 分开请求）
        → 每组发一次 FC3/FC4
        → 接收寄存器值 ushort[]，按点位拆成 N 段
        → 每段拼入 RawPointValue { RawData = ushort[] segment }
        → 不做 Endian 转换、不做类型转换（那是 Collection 的事）

    // 写
    WriteAsync(point, value)
        → 先按 DataType + Endian 编码 value → byte[]
        → FC6(单个) 或 FC16(批量)
}
```

### ModbusEndian

```csharp
public enum ModbusEndian
{
    ABCD,  // Big Endian（默认）
    CDAB,  // Word Swap
    BADC,  // Byte Swap
    DCBA   // Little Endian
}
```

Endian 信息通过 `DeviceConnection.Parameters["Endian"]` 传入，供 WriteAsync 编码和 Collection 的 PointValuePipeline 解码时使用。

### 错误处理

| Modbus 异常 | 返回 |
|---|---|
| 连接超时 | `OperationalError.Timeout` |
| 非法功能码 | `OperationalError.Protocol("非法功能码")` |
| 非法数据地址 | `OperationalError.Protocol("地址不存在")` |
| CRC 校验失败 | `OperationalError.Protocol("CRC 校验失败")` |

---

## 依赖

```
Protocol.Abstractions
    └── NitroGateway.Domain       DevicePoint（仅用于类型引用）
    └── NitroGateway.Shared       OperationResult

Protocol.Modbus
    ├── Protocol.Abstractions     IAddressParser / PointAddress / RawPointValue
    ├── NitroGateway.Domain       IProtocolDriver / ProtocolIdentifier
    ├── NitroGateway.Shared
    └── FluentModbus              NuGet
```

---

## 约束

1. 每个驱动实例绑定一个设备连接，不复用
2. `ReadBatchAsync` 内部自动按 Area/功能区分组，调用方不需要预分组
3. 个别点位字节拆分失败不阻塞同批其他点位，失败的跳过
4. `PingAsync` 复用已有读操作，不发专用 Ping 帧
5. 驱动层不做重试，重试是 Collection 层的策略
6. 驱动不持有 `DevicePoint.DataType` 以外的应用配置知识（Endian 通过 Parameters 传入）
7. Write 方向仍做编码（value → byte[]），因为这是协议必需步骤；Read 方向不做解码（byte[] → value），交给 Collection
