# PointValuePipeline 设计文档 v1

## 定位

值转换管道。`RawPointValue` → 类型转换 → 缩放 → 死区 → `PointSnapshot`。
纯函数，无 IO 副作用。死区缓存用内存 `ConcurrentDictionary`。

---

## 接口

```csharp
public interface IPointValuePipeline
{
    /// <summary>处理一批原始值，返回 PointSnapshot 列表。死区丢弃的不包含</summary>
    IReadOnlyList<PointSnapshot> Process(
        Guid deviceId,
        IReadOnlyList<RawPointValue> rawValues);

    /// <summary>获取上次工程值，null = 无历史值</summary>
    double? GetLastValue(Guid pointId);

    /// <summary>更新上次工程值缓存</summary>
    void SetLastValue(Guid pointId, double value);
}
```

> `deviceId` 作为参数传入而非从 `RawPointValue` 取，因为 `RawPointValue.Point`（DevicePoint）不持有 DeviceId。
> CollectionEngine 知道当前在采哪台设备，传给 Pipeline 即可。

## 处理步骤

```
RawPointValue { Point, RawData(object), Timestamp }
    │
    ├── 1. 类型转换
    │       RawData (Modbus → ushort[], OPCUA → Variant, S7 → byte[])
    │       → 按 Point.DataType + Endian(从 Connection.Parameters 取) 解析
    │       ushort[]{0x41A0,0x0000} + Float + ABCD → 12.5f
    │       ushort[]{0x0001} + Bool → true
    │       失败 → PointSnapshot { Quality=Bad, ErrorMessage="..." }
    │
    ├── 2. 缩放
    │       engineering = raw × Point.ScaleFactor + Point.ScaleOffset
    │       12.5 × 0.1 + 0 → 1.25
    │       失败 → PointSnapshot { Quality=Uncertain, ErrorMessage="..." }
    │
    ├── 3. 死区（仅数值型：Float/Double/Int*/UInt*）
    │       Deadband == 0 → 跳过死区判定
    │       |new − last| < Deadband → 丢弃，不加入结果
    │       通过 → SetLastValue(pointId, newValue)
    │
    └── 4. 组装
            PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = point.Id,
                RawValue = rawValue,
                Value = engineeringValue,
                Timestamp = rawValue.Timestamp,
                Quality = Good,
                ErrorMessage = null
            }
```

## 实现

```csharp
public sealed class PointValuePipeline : IPointValuePipeline
{
    private readonly ConcurrentDictionary<Guid, double> _lastValues = new();

    public IReadOnlyList<PointSnapshot> Process(
        Guid deviceId, IReadOnlyList<RawPointValue> rawValues)
    {
        var results = new List<PointSnapshot>(rawValues.Count);

        foreach (var raw in rawValues)
        {
            var snapshot = ConvertSingle(deviceId, raw);
            if (snapshot is not null)
                results.Add(snapshot);
        }
        return results;
    }

    private PointSnapshot? ConvertSingle(Guid deviceId, RawPointValue raw)
    {
        var point = raw.Point;

        // 1. 类型转换
        var (success, rawValue, error) = ParseRawValue(raw.RawData, point.DataType);
        if (!success)
            return new PointSnapshot
            {
                DeviceId = deviceId, DevicePointId = point.Id,
                RawValue = raw.RawData, Quality = QualityCode.Bad, ErrorMessage = error,
                Timestamp = raw.Timestamp
            };

        // 2. 缩放
        var engValue = (double)Convert.ChangeType(rawValue!, typeof(double))
                       * point.ScaleFactor + point.ScaleOffset;

        // 3. 死区
        if (point.Deadband > 0 && _lastValues.TryGetValue(point.Id, out var last))
        {
            if (Math.Abs(engValue - last) < point.Deadband)
                return null; // 丢弃
        }
        _lastValues[point.Id] = engValue;

        // 4. 组装
        return new PointSnapshot
        {
            DeviceId = deviceId, DevicePointId = point.Id,
            RawValue = rawValue, Value = engValue,
            Timestamp = raw.Timestamp,
            Quality = QualityCode.Good
        };
    }

    public double? GetLastValue(Guid pointId) =>
        _lastValues.TryGetValue(pointId, out var v) ? v : null;

    public void SetLastValue(Guid pointId, double value) =>
        _lastValues[pointId] = value;
}
```

## 约束

| 约束 | 说明 |
|---|---|
| 纯函数 | Process 不调 IO、不拿锁，只有 _lastValues 是可变状态 |
| 单点失败不阻塞 | 一个点位转换失败，其他继续 |
| 死区默认不启用 | Deadband = 0 时跳过 |
| 非数值型不判死区 | Bool/String 直接跳过死区判定 |

---

## 演进

| v1 | 简单类型转换 + 线性缩放 + 死区 | **当前** |
| v2 | 查表映射 + 非线性校准曲线 | 校准表需求 |
| v3 | 表达式引擎 | 计算式可配置 |
| v4 | 多传感器融合 | 双传感器校验 |
| v5 | 异常检测（3σ） | 值跳变标记 Uncertain |
