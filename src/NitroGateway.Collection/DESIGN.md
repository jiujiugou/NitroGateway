# Collection 模块设计文档 v1

## 定位

采集引擎。网关的"发动机"，负责从设备读取数据、转换为工程值、写入存储、上报健康。
四个子模块串联成一条流水线。

---

## 子模块

```
NitroGateway.Collection/
│
├── IDeviceReader.cs            数据读取
├── DeviceReader.cs             
│
├── IPointValuePipeline.cs      值转换管道
├── PointValuePipeline.cs       
│
├── IDataDispatcher.cs          数据分发
├── DataDispatcher.cs           
│
├── IHealthReporter.cs          健康上报
├── HealthReporter.cs           
│
├── CollectionEngine.cs         编排：串联四个子模块，控制采集循环
└── CollectionServiceCollectionExtensions.cs
```

---

## 流水线

```
DeviceReader                PointValuePipeline        DataDispatcher            HealthReporter
    │                             │                        │                         │
    │── 拿设备列表 + 点位           │                        │                         │
    │   (DeviceManager)            │                        │                         │
    │                              │                        │                         │
    │── IProtocolDriver            │                        │                         │
    │   .ReadBatchAsync()          │                        │                         │
    │                              │                        │                         │
    │── List<RawPointValue> ──────→│                        │                         │
    │                              │                        │                         │
    │                              │── ushort[]→DataType     │                         │
    │                              │── ×Scale+Offset         │                         │
    │                              │── 死区判定               │                         │
    │                              │                        │                         │
    │                              │── List<PointSnapshot> ─→│                         │
    │                              │                        │                         │
    │                              │                        │── TimeSeries.Write()    │
    │                              │                        │── Buffer.Enqueue()     │
    │                              │                        │                        │
    │                              │                        │── 成功 ────────────────→│ ReportSuccess()
    │                              │                        │── 失败 ────────────────→│ ReportFailure()
```

---

## 接口

### IDeviceReader — 数据读取

```csharp
/// <summary>从设备读取原始数据。依赖 DeviceManager 拿设备列表，IProtocolDriverFactory 拿驱动</summary>
public interface IDeviceReader
{
    /// <summary>对单台设备执行一轮采集，返回原始值列表</summary>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        Device device, CancellationToken ct);
}
```

内部流程：
1. 从 `Device.Points` 拿点位列表（只取 `Enabled = true` 的）
2. 通过 `IProtocolDriverFactory.Create(device.Protocol, device.Connection)` 拿驱动
3. 调 `driver.ConnectAsync()` → `driver.ReadBatchAsync(points)` → `driver.DisconnectAsync()`
4. 返回 `List<RawPointValue>`

v1 固定轮询 + 串行，不支持批量优化分组。v2 加 `PointBatchOptimizer`。

### IPointValuePipeline — 值转换管道

```csharp
/// <summary>原始值 → 工程值转换。纯函数，无副作用</summary>
public interface IPointValuePipeline
{
    /// <summary>
    /// 处理一批原始值，返回 PointSnapshot 列表。
    /// 死区判定丢弃的点位不包含在结果中。
    /// </summary>
    IReadOnlyList<PointSnapshot> Process(IReadOnlyList<RawPointValue> rawValues);

    /// <summary>获取上次工程值，用于死区判定。null 表示无历史值</summary>
    double? GetLastValue(Guid pointId);

    /// <summary>更新上次工程值缓存</summary>
    void SetLastValue(Guid pointId, double value);
}
```

处理步骤：
```
RawPointValue.RawData (ushort[] for Modbus)
    │
    ├── 1. 按 DataType + Endian 拼值
    │       ushort[]{0x41A0, 0x0000} → 12.5f
    │       失败 → PointSnapshot { Quality=Bad, ErrorMessage }
    │
    ├── 2. 缩放
    │       raw × ScaleFactor + ScaleOffset
    │       失败 → PointSnapshot { Quality=Uncertain, ErrorMessage }
    │
    ├── 3. 死区
    │       |new − last| < Deadband → 丢弃
    │
    └── 4. 组装
            PointSnapshot { DeviceId, DevicePointId, RawValue, Value, Timestamp, Quality }
```

### IDataDispatcher — 数据分发

```csharp
/// <summary>数据分发。写时序库 + 入转发缓冲。两个操作独立，一个失败不阻塞另一个</summary>
public interface IDataDispatcher
{
    /// <summary>分发一批快照</summary>
    Task<OperationResult> DispatchAsync(
        Guid deviceId,
        IReadOnlyList<PointSnapshot> snapshots,
        CancellationToken ct);
}
```

内部：
1. `TimeSeries.WriteAsync(snapshots)` — 写入失败不阻塞转发入队
2. 构造 `BatchMeasurements` → `Buffer.EnqueueAsync(batch)` — 入队失败不阻塞时序写入
3. 两个都成功返回 Success，任一失败返回 Error（含具体哪个失败）

### IHealthReporter — 健康上报

```csharp
/// <summary>向 DeviceHealthMonitor 上报本轮采集结果</summary>
public interface IHealthReporter
{
    /// <summary>上报一轮采集结果</summary>
    void Report(Guid deviceId, int successCount, int failCount, string? errorMessage);
}
```

内部：
- 全部成功 → `HealthMonitor.ReportSuccess(deviceId)`
- 有任何失败 → `HealthMonitor.ReportFailure(deviceId, "...")`

---

## CollectionEngine — 编排层

```csharp
/// <summary>采集引擎。串联四个子模块，控制主采集循环</summary>
public sealed class CollectionEngine
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceReader _reader;
    private readonly IPointValuePipeline _pipeline;
    private readonly IDataDispatcher _dispatcher;
    private readonly IHealthReporter _reporter;

    /// <summary>对一台设备执行一轮完整采集</summary>
    public async Task CollectDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        // 1. 拿设备
        var deviceResult = await _deviceManager.GetAsync(deviceId, ct);
        if (deviceResult.IsFailure) return;

        // 2. 读
        var readResult = await _reader.ReadDeviceAsync(deviceResult.Value!, ct);
        if (readResult.IsFailure)
        {
            _reporter.Report(deviceId, 0, 1, readResult.Error!.Message);
            return;
        }

        // 3. 转换
        var snapshots = _pipeline.Process(readResult.Value!);
        var successCount = snapshots.Count(s => s.Quality == QualityCode.Good);
        var failCount = snapshots.Count - successCount;

        // 4. 分发
        await _dispatcher.DispatchAsync(deviceId, snapshots, ct);

        // 5. 健康
        _reporter.Report(deviceId, successCount, failCount, null);
    }

    /// <summary>主循环：对所有 Online 设备轮询采集</summary>
    public async Task RunAsync(int intervalMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var devices = await _deviceManager.GetByStatusAsync(DeviceStatus.Online, ct);
            if (devices.IsSuccess)
            {
                foreach (var device in devices.Value!)
                    await CollectDeviceAsync(device.Id, ct);
            }
            await Task.Delay(intervalMs, ct);
        }
    }
}
```

---

## 约束

1. **v1 固定轮询 + 串行** — 一台设备一台设备来，不并行
2. **Pipeline 是纯函数** — 不调 IO，不拿锁，输入输出确定
3. **死区缓存用内存** — `ConcurrentDictionary<Guid, double>` 存上次工程值，重启后丢失
4. **分发双写** — TimeSeries 和 Buffer 互相独立，一个失败不阻塞另一个
5. **Reader 每次连接/断开** — v1 不保活，一轮采集一次 TCP 连接
6. **只采 Online 设备** — Unknown/Offline/Error/Maintenance 跳过

---

## 依赖

```
NitroGateway.Collection
    ├── NitroGateway.Domain              RawPointValue / PointSnapshot / Device / DevicePoint
    ├── NitroGateway.Shared              OperationResult
    ├── NitroGateway.Device              IDeviceManager / IPointManager / IDeviceHealthMonitor
    ├── Protocol.Abstractions            IProtocolDriverFactory
    ├── Storage.TimeSeries               IMeasurementStore
    └── Storage.Buffer                   IForwardBuffer
```
