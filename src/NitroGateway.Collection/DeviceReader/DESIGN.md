# DeviceReader 设计文档 v1

## 定位

从设备读取原始数据。通过 `IProtocolDriverFactory` 创建对应协议驱动，调 `ReadBatchAsync` 获取 `RawPointValue` 列表。

---

## 接口

```csharp
public interface IDeviceReader
{
    /// <summary>对单台设备执行一轮采集，返回原始值列表</summary>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        Device device, CancellationToken ct);
}
```

## 实现

```csharp
public sealed class DeviceReader : IDeviceReader
{
    private readonly IProtocolDriverFactory _driverFactory;

    public DeviceReader(IProtocolDriverFactory driverFactory)
    {
        _driverFactory = driverFactory;
    }

    public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        Device device, CancellationToken ct)
    {
        // 1. 取启用的点位
        var points = device.Points.Where(p => p.Enabled).ToList();
        if (points.Count == 0)
            return Array.Empty<RawPointValue>();

        // 2. 创建驱动
        var driver = _driverFactory.Create(device.Protocol, device.Connection);

        // 3. 连接
        var connectResult = await driver.ConnectAsync(ct);
        if (connectResult.IsFailure)
            return connectResult.Error!;

        try
        {
            // 4. 批量读
            var readResult = await driver.ReadBatchAsync(points, ct);
            return readResult.IsSuccess
                ? readResult.Value!
                : readResult.Error!;
        }
        finally
        {
            // 5. 断开
            await driver.DisconnectAsync(ct);
        }
    }
}
```

## 关键决策

| 决策 | 做法 | 原因 |
|---|---|---|
| 每轮一连接 | Connect → Read → Disconnect | v1 简单，v2 加连接池 |
| 全量读 | 一次读所有 Enabled 点位 | v2 按间隔分组 |
| 失败即停 | 读失败直接返回 error | v1 无重试，上层决定 |
| 空点位跳过 | points.Count == 0 直接返回 | 避免空批量请求 |

---

## 演进

| v1 | 串行轮询，固定间隔，读全量 | **当前** |
| v2 | 按点位 ScanIntervalMs 分组，独立采样 | 点位 > 500 |
| v3 | 按协议能力分流：OPC UA 订阅 / Modbus 轮询 | 接入 OPC UA |
| v4 | 自适应采样：变化剧烈提频，平稳降频 | 带宽瓶颈 |
| v5 | 跨设备协调：同 485 总线避免帧碰撞 | 接入 RTU 多从站 |
