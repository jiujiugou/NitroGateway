using System.Collections.Concurrent;
using System.Diagnostics;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Telemetry.Tracing;

namespace NitroGateway.Collection;

/// <summary>
/// 值转换管道实现：协议解码值 → 工程缩放（×ScaleFactor + ScaleOffset）→ 死区 → PointSnapshot。
/// 协议解码由驱动完成（Modbus ushort[]→类型、OPC UA Variant→.NET 类型），本类不感知协议细节。
/// <para><b>死区语义（重要）：</b>本管道<u>不丢数据</u>——死区只影响"上次工程值缓存"的更新
/// （供告警 Duration 判定），并把 <see cref="DevicePoint.Deadband"/> 透传到快照
/// （<see cref="PointSnapshot.Deadband"/>）。真正的变化抑制由 Dispatcher 层的
/// <see cref="ChangeDetector"/> 执行（ADR-053），三处消费边界（落库/转发/SignalR）共用放行子集，
/// 桌面实时图与告警仍收全量。</para>
/// <para><b>边界：</b>Bool/String 不做缩放与死区；非数值类型直接透传；
/// 数值缩放失败产出 Uncertain 快照而非抛异常；单点位失败不影响整批。</para>
/// </summary>
public sealed class PointValuePipeline : IPointValuePipeline
{
    /// <summary>上次工程值缓存（内存态，重启丢失）。仅数值点位有记录。</summary>
    private readonly ConcurrentDictionary<Guid, double> _lastValues = new();

    /// <inheritdoc />
    /// <remarks>逐点位独立转换：单点失败（缩放异常）返回 Uncertain 快照，其他点位不受影响。</remarks>
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

    /// <summary>
    /// 处理单个值：按数据类型走透传/缩放，再更新死区缓存。
    /// </summary>
    /// <param name="deviceId">所属设备 ID</param>
    /// <param name="raw">原始值（含点位定义与驱动解码后的值）</param>
    /// <returns>转换后的快照；数值缩放失败时返回 Uncertain 质量快照</returns>
    private PointSnapshot? ConvertSingle(Guid deviceId, RawPointValue raw)
    {
        var point = raw.Point;
        var rawValue = raw.Value;

        // 1. 非数值型 → 直接输出（Bool/String）
        if (point.DataType is DataType.Bool or DataType.String)
        {
            return new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = point.Id,
                PointName = point.Name,
                DataType = point.DataType,
                Access = point.Access,
                RawValue = rawValue,
                Value = rawValue,
                Timestamp = raw.Timestamp,
                Quality = QualityCode.Good,
                // ADR-053：Bool/String 无死区概念，但统一透传 Deadband 供 ChangeDetector 读取
                Deadband = point.Deadband
            };
        }

        // 2. 缩放
        if (!IsNumericType(point.DataType))
        {
            return new PointSnapshot
            {
                DeviceId = deviceId,
                DevicePointId = point.Id,
                PointName = point.Name,
                DataType = point.DataType,
                Access = point.Access,
                RawValue = rawValue,
                Value = rawValue,
                Timestamp = raw.Timestamp,
                Quality = QualityCode.Good,
                // ADR-053：非数值类型同样透传 Deadband（ChangeDetector 统一按快照字段判定）
                Deadband = point.Deadband
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
                PointName = point.Name,
                DataType = point.DataType,
                Access = point.Access,
                RawValue = rawValue,
                Timestamp = raw.Timestamp,
                Quality = QualityCode.Uncertain,
                // ADR-053：缩放失败按"质量变化必写"落库；透传 Deadband 供 ChangeDetector 读取
                Deadband = point.Deadband,
                ErrorMessage = "缩放失败：无法转换为数值"
            };
        }

        // 3. 死区判定：更新 _lastValues 缓存（供告警 Duration 使用），但不丢弃数据
        //    数据照常往下游传送，SignalR 推送和存储写入不受死区影响
        if (point.Deadband > 0 &&
            _lastValues.TryGetValue(point.Id, out var lastDead) &&
            Math.Abs(engValue - lastDead) < point.Deadband)
        {
            // 值在死区内，不更新缓存（避免微小波动被 Duration 告警误判）
        }
        else
        {
            _lastValues[point.Id] = engValue;
        }

        // 4. 组装
        return new PointSnapshot
        {
            DeviceId = deviceId,
            DevicePointId = point.Id,
            PointName = point.Name,
            DataType = point.DataType,
            Access = point.Access,
            RawValue = rawValue,
            Value = engValue,
            Timestamp = raw.Timestamp,
            Quality = QualityCode.Good,
            // ADR-053：把点位级死区透传到快照，ChangeDetector 在 Dispatcher 层做变化抑制
            Deadband = point.Deadband
        };
    }

    /// <summary>判断点位类型是否为数值型（可参与缩放与死区判定）。</summary>
    /// <param name="type">点位数据类型</param>
    /// <returns>Float/Double/Int16..UInt64 返回 true，其余（Bool/String/未知）返回 false</returns>
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
