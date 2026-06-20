# OPC UA 模块设计文档 v1

## 定位

OPC UA 协议驱动，实现 `IProtocolDriver`。基于 `OPCFoundation.NetStandard.Opc.Ua` SDK，
管理 Session 生命周期，执行 Read/Write 操作。
v1 轮询模式，v2 加 Subscription 推送。

---

## 项目结构

```
NitroGateway.Protocol.OpcUa/
├── NitroGateway.Protocol.OpcUa.csproj
├── OpcUaAddress.cs                PointAddress 子类
├── OpcUaAddressParser.cs          "ns=3;s=Temperature" → OpcUaAddress
├── OpcUaDriver.cs                 IProtocolDriver 实现
├── OpcUaDriverCapability.cs       能力声明
├── IBrowseableDriver.cs           Browse 能力接口（独立于 IProtocolDriver）
└── OpcUaServiceCollectionExtensions.cs
```

---

## 职责边界（关键修正）

```
ModbusDriver 负责                  OpcUaDriver 负责
───────────────────────            ───────────────────────
ushort[] → int/float/bool          Variant → double/string/bool
Endian 转换                        类型映射 (UA Type → .NET Type)
返回 RawPointValue{Value=25.3f}    返回 RawPointValue{Value=85.3}

Pipeline 只看到 "double" 或 "int"，不知道 Modbus 还是 OPC UA
```

---

## 地址模型

### OpcUaAddress

```csharp
/// <summary>OPC UA NodeId 地址。对应真实 NodeId 结构，不存裸 string</summary>
public sealed record OpcUaAddress : PointAddress
{
    /// <summary>命名空间索引，默认 0</summary>
    public ushort NamespaceIndex { get; init; }

    /// <summary>字符串标识符（ns=3;s=xxx → "xxx"）</summary>
    public string? StringId { get; init; }

    /// <summary>数字标识符（ns=2;i=1001 → 1001）</summary>
    public uint? NumericId { get; init; }

    /// <summary>GUID 标识符（ns=4;g=xxx）</summary>
    public Guid? GuidId { get; init; }

    /// <summary>Opaque 标识符（ns=5;b=xxx）</summary>
    public byte[]? OpaqueId { get; init; }
}
```

不是简单存 `string Address`——结构直接对应 OPC UA NodeId 规范。
AddressParser 解析后构造这个对象，调用方用模式匹配或强类型属性访问。

---

## 接口

### IProtocolDriver（已有，不动）

```csharp
Task<OperationResult> ConnectAsync(CancellationToken ct);
Task<OperationResult> DisconnectAsync(CancellationToken ct);
Task<OperationResult> PingAsync(CancellationToken ct);
Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct);
Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct);
Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct);
```

### IBrowseableDriver — 独立接口

```csharp
/// <summary>
/// 节点 Browse 能力。和 IProtocolDriver 分离——
/// 采集引擎不调这个，配置/导入工具调。
/// </summary>
public interface IBrowseableDriver
{
    /// <summary>浏览指定节点下的子节点，返回可转为 DevicePoint 的节点列表</summary>
    Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default);
}

/// <summary>Browse 返回的节点信息</summary>
public sealed record BrowseNode
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string TypeName { get; init; }       // "Double" / "String" / "Boolean"
    public required bool IsVariable { get; init; }       // true=变量点, false=对象/文件夹
    public required string Access { get; init; }         // "Read" / "ReadWrite"
}
```

---

## 驱动实现

### OpcUaDriver — Session 生命周期

```
ConnectAsync()
    ├── 解析 Endpoint: opc.tcp://server:4840
    ├── ApplicationConfiguration init
    ├── CreateSession()
    ├── ActivateSession()
    └── State = Connected

DisconnectAsync()
    ├── CloseSession()
    ├── Disconnect()
    └── State = Disconnected

PingAsync()
    └── 读 ServerStatus 节点 → 成功即连通
```

### ReadAsync / ReadBatchAsync

```
ReadBatchAsync(points)
    ├── 每个 point.Address → OpcUaAddressParser.Parse()
    ├── 构造 ReadValueIdCollection（可以一次请求多个 NodeId）
    ├── session.Read(request)  → DataValueCollection
    ├── 每个 DataValue:
    │   ├── Variant → .NET Type (double/int/string/bool)
    │   ├── StatusCode → Quality（Good/Uncertain/Bad）
    │   └── SourceTimestamp → DateTime
    └── 返回 List<RawPointValue>
```

**RawPointValue.Value 已经是 double/int/string/bool，不是 Variant。**
Pipeline 不做任何协议解码。

---

## DriverCapability

```csharp
public static class OpcUaDriverCapability
{
    public static readonly DriverCapability Instance = new()
    {
        SupportsBatchRead = true,
        SupportsBatchWrite = true,
        SupportsSubscription = true,    // v2 启用
        MaxBatchSize = 0                // 无限制
    };
}
```

---

## Session 生命周期 v1

```
正常流程：
  ConnectAsync → Session 创建 → Read/Write → DisconnectAsync → Session 关闭

异常流程：
  Read 失败 → 返回 OperationResult.Error → HealthReporter 报警
  Session 过期 → 返回 OperationalError（v1 不做自动重连）
  Scheduler 下次轮询 → 重新尝试 Read
```

---

## 调用时序

```
Scheduler 触发 Collection:

1. DeviceReader.ReadDeviceAsync(device)
     ├── OpcUaDriver.ConnectAsync()
     │     ├── CreateSession()
     │     └── ActivateSession()
     │
     ├── OpcUaDriver.ReadBatchAsync(points)
     │     ├── 构造 ReadValueIdCollection
     │     ├── session.Read()
     │     ├── Variant→.NET type 解码
     │     └── List<RawPointValue> { Value=85.3, Value=2.1, ... }
     │
     ├── OpcUaDriver.DisconnectAsync()
     │     ├── CloseSession()
     │     └── Disconnect()
     │
     └── Pipeline.Process(deviceId, rawValues)
           ├── 缩放: 85.3 × 1.0 = 85.3
           ├── 死区
           └── PointSnapshot
```

---

## AddressParser

```
输入格式:
  ns=3;s=Temperature          → OpcUaAddress { NamespaceIndex=3, StringId="Temperature" }
  ns=2;i=1001                 → OpcUaAddress { NamespaceIndex=2, NumericId=1001 }
  ns=4;g={guid}               → OpcUaAddress { NamespaceIndex=4, GuidId=... }
  ns=1;b=base64               → OpcUaAddress { NamespaceIndex=1, OpaqueId=... }
```

---

## NuGet

`OPCFoundation.NetStandard.Opc.Ua` — 官方 SDK，处理创建 Session、拼帧、加密、序列化。
不手写 OPC UA 协议。

---

## 约束

1. **v1 无安全策略** — 使用 None Profile，匿名连接
2. **v1 轮询模式** — 不用 Subscription 推送
3. **Session 不自动重连** — 断开返回 Error，上层重试
4. **Browse 独立接口** — 不污染 IProtocolDriver
5. **Pipeline 不做协议解码** — Variant→.NET Type 在驱动内完成
6. **每轮一 Session** — v1 不保活，Connect→Read→Disconnect
7. **RawPointValue.Value 是领域值** — double/int/string/bool，不是 Variant
