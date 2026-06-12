# Device 模块设计文档

## 定位

设备管理领域服务（配置期职责）。负责设备的注册、配置、状态监控与点位管理。
依赖 Domain.Devices（领域模型）和 Shared（OperationResult），调 Storage 做持久化。

**运行期职责**（值转换、批量优化）属于 Collection 模块，不在本模块范围内。

---

## 子模块

```
NitroGateway.Device/
├── DeviceManager.cs          设备 CRUD + 生命周期
├── PointManager.cs           点位配置 + 校验
├── DeviceHealthMonitor.cs    健康判定：采集成败计数 → 阈值触发状态变更
└── DeviceEvents.cs           领域事件（DeviceRegistered / DeviceOffline / DeviceOnline / PointChanged ...）
```

---

## 各模块职责边界

```
配置期（Device 模块）               运行期（Collection 模块）
─────────────────────────          ─────────────────────────────
DeviceManager                       CollectionEngine
  设备注册/注销/查询                   采集调度执行

PointManager                        PointBatchOptimizer
  点位 CRUD + 校验                    点位分组策略
                                     PointBatch
                                       分组结果
                                     PointValuePipeline
                                       值转换管道（类型→缩放→死区→快照）

DeviceHealthMonitor
  采集成败计数 + 阈值判定
  → 调 IDeviceManager.UpdateStatusAsync()
```

---

## 接口

### IDeviceManager — 设备生命周期

```csharp
// 注册/注销
Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct);
Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct);

// 查询
Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct);
Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct);
Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct);

// 状态（唯一入口，不允许外部直接赋值 device.Status）
Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct);
Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct);
```

### IPointManager — 点位管理

```csharp
// CRUD
Task<OperationResult<DevicePoint>> AddAsync(Guid deviceId, DevicePoint point, CancellationToken ct);
Task<OperationResult> RemoveAsync(Guid deviceId, Guid pointId, CancellationToken ct);
Task<OperationResult> UpdateAsync(Guid deviceId, DevicePoint point, CancellationToken ct);

// 批量导入
Task<OperationResult<IReadOnlyList<DevicePoint>>> ImportAsync(
    Guid deviceId, IReadOnlyList<DevicePoint> points, CancellationToken ct);

// 查询
Task<OperationResult<IReadOnlyList<DevicePoint>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct);

// 校验（地址格式委托 IAddressParser，它在 Protocol 层）
Task<OperationResult<IReadOnlyList<PointValidationError>>> ValidateAsync(
    Guid deviceId, DevicePoint point, CancellationToken ct);
```

`ValidateAsync` 调 `IAddressParser.Parse(point.Address)` 做格式校验，但 PointManager 不持有协议知识——只知道 "这个 parser 返回 true 就合法"。

### IDeviceHealthMonitor — 健康判定（只计数，不修改状态）

```csharp
// Collection 每轮采集结束后调用
void ReportSuccess(Guid deviceId);
void ReportFailure(Guid deviceId, string reason);

// 阈值配置
int FailureThreshold { get; }    // 默认 10
int RecoveryThreshold { get; }   // 默认 3

// 可观测
int GetConsecutiveFailures(Guid deviceId);
int GetConsecutiveSuccesses(Guid deviceId);
```

阈值触发后调 `IDeviceManager.UpdateStatusAsync()` 写入，确保走统一门控。自身不直接改 `Device.Status`。

---

## 地址解析（Protocol 层）

`IAddressParser` 和 `PointAddress` 定义在 Protocol 层，不属于 Device：

```
Protocol/
├── IAddressParser.cs          接口
├── PointAddress.cs            解析结果值对象
├── ModbusAddressParser.cs     "40001" → {Offset:0, Func:HoldingRegister}
├── OpcUaAddressParser.cs      "ns=3;s=Temp" → {Namespace:3, Id:"Temp"}
└── S7AddressParser.cs         "DB1.DBD0" → {DB:1, Offset:0, Size:4}
```

```csharp
// Protocol 层定义
public interface IAddressParser
{
    PointAddress Parse(string rawAddress);
    string Serialize(PointAddress address);
    int GetDistance(PointAddress a, PointAddress b);
}

public sealed class PointAddress
{
    public string Raw { get; init; }
    public required Dictionary<string, object> Parts { get; init; }
}
```

PointManager 通过 `IAddressParser` 做校验，Collection 的 `PointBatchOptimizer` 用 `GetDistance` 做分组。两者都依赖这个接口，但接口本身不放在 Domain。

---

## 值转换管道（Collection 层）

`PointValuePipeline` 属于 Collection 模块，不在 Device：

```
Collection/
└── PointValuePipeline.cs

驱动返回原始值
    ├── 1. 类型转换 ── Point.DataType 校验 → 失败则 Quality=Bad
    ├── 2. 缩放 ───── raw × ScaleFactor + ScaleOffset → 失败则 Quality=Uncertain
    ├── 3. 死区 ───── |新值−上次值| < Deadband → 丢弃，返回 null
    └── 4. 输出 ───── PointSnapshot { Value, Timestamp, Quality }
```

---

## 批量优化（Collection 层）

`PointBatchOptimizer` 和 `PointBatch` 属于 Collection 模块：

```
Collection/
├── PointBatchOptimizer.cs
└── PointBatch.cs
```

| 协议 | 分组规则 |
|---|---|
| Modbus | 连续寄存器且间隔 ≤ 16 → 合并一次批量读 |
| OPC UA | 多个 NodeId 合并为一次 Read 请求 |
| S7 | 同一 DB 块内连续地址 → 合并 |

Optimizer 依赖 `IAddressParser.GetDistance()` 判断地址连续性。

---

## 调用时序

### 健康状态判定

```
Collection 每轮采集结束:

DeviceHealthMonitor.ReportSuccess(deviceId)  或  ReportFailure(deviceId, reason)
    │
    ├── SuccessCount++ / FailCount++
    │
    ├── FailCount ≥ FailureThreshold(10)
    │   └── IDeviceManager.UpdateStatusAsync(deviceId, Offline)
    │       ├── IDeviceRepository.SaveAsync()
    │       └── 发布 DeviceOffline 事件
    │
    └── SuccessCount ≥ RecoveryThreshold(3)
        └── IDeviceManager.UpdateStatusAsync(deviceId, Online)
            ├── IDeviceRepository.SaveAsync()
            └── 发布 DeviceOnline 事件
```

---

## 新增领域模型

以下添加到 `Domain.Devices/`：

| 类型 | 说明 |
|---|---|
| `PointValidationError` | 点位校验错误（字段 + 消息） |
| `DeviceOfflineEvent` | 设备离线领域事件 |
| `DeviceOnlineEvent` | 设备恢复领域事件 |

以下放在 Protocol 层：

| 类型 | 说明 |
|---|---|
| `PointAddress` | 协议无关的地址结构 |
| `IAddressParser` | 地址解析接口 |

---

## 约束

1. **Status 单入口** — 任何地方不允许 `device.Status = xxx`，必须通过 `IDeviceManager.UpdateStatusAsync()`
2. **HealthMonitor 只计数** — 不修改 Device 状态，不持久化。状态变更委托 `IDeviceManager.UpdateStatusAsync`
3. **PointManager 不碰协议细节** — 地址校验委托 `IAddressParser`（Protocol 层），自己不解析地址
4. **ValidateConnectionAsync 不属于本模块** — 连接验证是 `IProtocolDriver.PingAsync()` 的职责
5. **Device 是配置期模块** — 值转换、批量优化、采集调度全部归 Collection 模块

---

## 依赖

```
NitroGateway.Device
    ├── NitroGateway.Domain              Device / DevicePoint / DeviceStatus / ProtocolIdentifier
    ├── NitroGateway.Shared              OperationResult / OperationalError
    ├── Storage.Configuration            接口 (IDeviceRepository)
    └── Protocol                         接口 (IAddressParser，仅校验用)
```

---

## 不负责

| 职责 | 归属模块 |
|---|---|
| 采集调度与执行 | Collection |
| 值转换管道（缩放/死区） | Collection |
| 点位批量优化 | Collection |
| 协议驱动实现 | Protocol.* |
| 地址解析 | Protocol |
| 连接验证 | Protocol (IProtocolDriver.PingAsync) |
| 告警规则判定 | Alarm |
| 数据转发 | Forwarder |
| MQTT/HTTP 通信 | Transport |
