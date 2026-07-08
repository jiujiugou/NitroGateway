using System.Collections.Concurrent;
using System.Diagnostics;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Collection;

/// <summary>值转换管道实现。Modbus ushort[] → 类型转换 → 缩放 → 死区 → PointSnapshot</summary>
public sealed class PointValuePipeline : IPointValuePipeline
{
    private readonly ConcurrentDictionary<Guid, double> _lastValues = new();

    /// <inheritdoc />
    public IReadOnlyList<PointSnapshot> Process(
        Guid deviceId, IReadOnlyList<RawPointValue> rawValues)
    {
        using var activity = GatewayActivitySource.Source.StartActivity(GatewayActivities.Pipeline);
        activity?.SetTag(GatewayActivityTags.DeviceId, deviceId.ToString());
        activity?.SetTag(GatewayActivityTags.SnapshotCount, rawValues.Count);

        var results = new List<PointSnapshot>(rawValues.Count);
        foreach (var raw in rawValues)
        {
            var snapshot = ConvertSingle(deviceId, raw);
            if (snapshot is not null)
                results.Add(snapshot);
        }
        activity?.SetStatus(ActivityStatusCode.Ok);
        return results;
    }

    /// <inheritdoc />
    public double? GetLastValue(Guid pointId) =>
        _lastValues.TryGetValue(pointId, out var v) ? v : null;

    /// <inheritdoc />
    public void SetLastValue(Guid pointId, double value) =>
        _lastValues[pointId] = value;

    // ---- 内部 ----

    /// <summary>处理单个值：缩放 + 死区。不做协议解码（驱动已完成）</summary>
    private PointSnapshot? ConvertSingle(Guid deviceId, RawPointValue raw)
    {
        var point = raw.Point;
        var rawValue = raw.Value;

        // 1. 非数值型 → 直接输出（Bool/String）
        if (point.DataType is DataType.Bool or DataType.String)
        {
            return new PointSnapshot
            {
                DeviceId = deviceId, DevicePointId = point.Id,
                RawValue = rawValue, Value = rawValue,
                Timestamp = raw.Timestamp, Quality = QualityCode.Good
            };
        }

        // 2. 缩放
        if (!IsNumericType(point.DataType))
        {
            return new PointSnapshot
            {
                DeviceId = deviceId, DevicePointId = point.Id,
                RawValue = rawValue, Value = rawValue,
                Timestamp = raw.Timestamp, Quality = QualityCode.Good
            };
        }

        double engValue;
        try
        {
            var d = Convert.ToDouble(rawValue);
            engValue = d * point.ScaleFactor + point.ScaleOffset;
        }
        catch
        {
            return new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = point.Id,
                RawValue = rawValue,
                Timestamp = raw.Timestamp,
                Quality = QualityCode.Uncertain,
                ErrorMessage = "缩放失败：无法转换为数值"
            };
        }

        // 3. 死区（仅数值型）
        if (point.Deadband > 0 && IsNumericType(point.DataType))
        {
            if (_lastValues.TryGetValue(point.Id, out var last) &&
                Math.Abs(engValue - last) < point.Deadband)
                return null;

            _lastValues[point.Id] = engValue;
        }

        // 4. 组装
        return new PointSnapshot
        {
            DeviceId = deviceId,
            DevicePointId = point.Id,
            RawValue = rawValue,
            Value = engValue,
            Timestamp = raw.Timestamp,
            Quality = QualityCode.Good
        };
    }

    private static bool IsNumericType(DataType type) => type switch
    {
        DataType.Float => true,
        DataType.Double => true,
        DataType.Int16 => true,
        DataType.UInt16 => true,
        DataType.Int32 => true,
        DataType.UInt32 => true,
        DataType.Int64 => true,
        DataType.UInt64 => true,
        _ => false
    };
}
