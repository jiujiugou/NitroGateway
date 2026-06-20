# DataDispatcher 设计文档 v1

## 定位

数据分发。`PointSnapshot` → 写时序库（IMeasurementStore）+ 入转发缓冲（IForwardBuffer）。
两个操作独立，一个失败不阻塞另一个。

---

## 接口

```csharp
public interface IDataDispatcher
{
    Task<OperationResult> DispatchAsync(
        Guid deviceId,
        IReadOnlyList<PointSnapshot> snapshots,
        CancellationToken ct);
}
```

## 实现

```csharp
public sealed class DataDispatcher : IDataDispatcher
{
    private readonly IMeasurementStore _timeSeries;
    private readonly IForwardBuffer _buffer;
    private readonly ILogger<DataDispatcher> _logger;

    public DataDispatcher(
        IMeasurementStore timeSeries,
        IForwardBuffer buffer,
        ILogger<DataDispatcher> logger)
    {
        _timeSeries = timeSeries;
        _buffer = buffer;
        _logger = logger;
    }

    public async Task<OperationResult> DispatchAsync(
        Guid deviceId,
        IReadOnlyList<PointSnapshot> snapshots,
        CancellationToken ct)
    {
        if (snapshots.Count == 0) return OperationResult.Success();

        var tsOk = false;
        var bufOk = false;

        // 1. 写时序库
        var tsResult = await _timeSeries.WriteAsync(snapshots, ct);
        if (tsResult.IsSuccess) tsOk = true;
        else _logger.LogWarning("时序写入失败: {Error}", tsResult.Error!.Message);

        // 2. 入转发缓冲
        var batch = ToBatchMeasurements(deviceId, snapshots);
        var bufResult = await _buffer.EnqueueAsync(batch, ct);
        if (bufResult.IsSuccess) bufOk = true;
        else _logger.LogWarning("缓冲入队失败: {Error}", bufResult.Error!.Message);

        return tsOk && bufOk
            ? OperationResult.Success()
            : OperationalError.General(
                $"分发部分失败: 时序={(tsOk ? "OK" : "FAIL")} 缓冲={(bufOk ? "OK" : "FAIL")}");
    }

    private static BatchMeasurements ToBatchMeasurements(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        return new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            ScanStartedAt = now,
            ScanCompletedAt = now,
            Records = snapshots.Select(s => new MeasurementRecord
            {
                Id = Guid.NewGuid(),
                DeviceId = s.DeviceId,
                DevicePointId = s.DevicePointId,
                PointName = "",
                Value = s.Value,
                DataType = Domain.Devices.DataType.Float,
                Timestamp = s.Timestamp,
                ReceivedAt = now,
                Quality = s.Quality
            }).ToList()
        };
    }
}
```

## 关键决策

| 决策 | 做法 | 原因 |
|---|---|---|
| 双写独立 | TimeSeries 失败 ≠ Buffer 失败 | 时序落了数据就不丢 |
| 不重试 | 失败只记日志 | v1 简单，v2 加重试策略 |
| 空快照跳过 | snapshots.Count == 0 → 直接返回 | 避免空批次进 Buffer |
| PointName/DataType 留空 | v1 不补，后续从缓存补 | 减少依赖 |

---

## 演进

| v1 | 逐条写时序 + 逐批入缓冲 | **当前** |
| v2 | 事务批量写入时序 | 写入量 > 1000/s |
| v3 | 降采样预聚合 | 查询压力大 |
| v4 | 压缩编码 (delta-of-delta + XOR) | 存储成本敏感 |
| v5 | 分库分表 + 冷热分层 | 设备 > 1000, 数据 > TB |
