using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Security.Guard;
using NitroGateway.Shared;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.DeviceManagement;

/// <summary>
/// 写服务实现（docs/14 §3.2）。Web 写端点与桌面 RealtimeViewModel 共用同一写链路：
/// 设备/点位（IDeviceSnapshotCache）→ Access/Enabled 校验 → 值类型转换（按 DataType）→
/// WriteGuard 三级门控 → 反向缩放（工程值 → 原始值）→ 驱动池取长连接 → WriteAsync。
///
/// 设计决策：
/// <list type="bullet">
/// <item>依赖全部为 Singleton（目录缓存/健康监控/时序存储/驱动池/门控），服务注册为 Singleton，桌面与 Web 直接注入。</item>
/// <item>String 类型不做范围/变化率校验（WriteCommand.Value 是 double，字符串无法参与）——只做设备在线（Mode）校验。</item>
/// <item>Bool 按 0/1 参与范围/变化率校验（Bool 点位范围通常配 0~1），实际写驱动时再转回 bool。</item>
/// <item>写的是工程值：采集侧 工程值 = 原始值 × ScaleFactor + ScaleOffset，写侧反向缩放后再调驱动。</item>
/// </list>
/// </summary>
public sealed class WriteService : IWriteService
{
    private readonly IDeviceSnapshotCache _cache;
    private readonly IDeviceHealthMonitor _health;
    private readonly IMeasurementStore _store;
    private readonly IProtocolDriverPool _pool;
    private readonly WriteGuard _guard;
    private readonly ModeValidator _mode;
    private readonly ILogger<WriteService> _logger;

    public WriteService(
        IDeviceSnapshotCache cache,
        IDeviceHealthMonitor health,
        IMeasurementStore store,
        IProtocolDriverPool pool,
        WriteGuard guard,
        ModeValidator mode,
        ILogger<WriteService> logger)
    {
        _cache = cache;
        _health = health;
        _store = store;
        _pool = pool;
        _guard = guard;
        _mode = mode;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OperationResult> WriteAsync(WriteRequest request, CancellationToken ct = default)
    {
        // ── 1. 设备 + 点位（目录缓存为内存快照，Web/桌面共用）──
        var devices = await _cache.GetAllAsync(ct);
        if (devices.IsFailure)
            return devices.Error!;
        var device = devices.Value!.FirstOrDefault(d => d.Id == request.DeviceId);
        if (device is null)
            return OperationalError.NotFound($"设备不存在：{request.DeviceId}");
        var point = device.Points.FirstOrDefault(p => p.Id == request.PointId);
        if (point is null)
            return OperationalError.NotFound($"点位不存在：{request.PointId}");

        // ── 2. Access / Enabled 校验 ──
        if (point.Access is not (PointAccess.WriteOnly or PointAccess.ReadWrite))
            return OperationalError.Validation($"点位「{point.Name}」为只读，无法写入");
        if (!point.Enabled)
            return OperationalError.Validation($"点位「{point.Name}」已禁用，无法写入");

        // ── 3. 值类型转换（按 DataType；输入可能是 JsonElement / 字符串 / number）──
        var converted = ConvertValue(point.DataType, request.Value);
        if (converted.IsFailure)
            return converted.Error!;
        var typedValue = converted.Value!;

        // ── 4. WriteGuard 校验 ──
        // 设备在线状态以 IDeviceHealthMonitor 实时快照为准（采集 SST），无快照时回退设备配置状态（更保守，Unknown 会被拒绝）。
        var status = _health.GetSnapshot(request.DeviceId)?.Status.ToString() ?? device.Status.ToString();
        var previous = await _store.QueryLatestAsync(request.DeviceId, request.PointId, ct);
        double? previousValue = null;
        if (previous.IsSuccess && previous.Value is { Count: > 0 })
            previousValue = TryToDouble(previous.Value[0].Value);

        var guardResult = EvaluateGuard(request.DeviceId, point, typedValue, status, previousValue);
        if (guardResult.IsFailure)
        {
            _logger.LogWarning("写指令未通过门控: Device={DeviceId} Point={PointId} Error={Error}",
                request.DeviceId, request.PointId, guardResult.Error!.Message);
            return guardResult.Error!;
        }

        // ── 5. 驱动写：取长连接驱动（未连接先建连），写原始值（工程值反向缩放）──
        var driver = _pool.GetOrCreate(device);
        if (driver.State != DriverState.Connected)
        {
            var connect = await driver.ConnectAsync(ct);
            if (connect.IsFailure)
                return OperationalError.Communication($"设备连接失败：{connect.Error!.Message}");
        }

        var writeValue = ToRawValue(point, typedValue);
        var result = await driver.WriteAsync(point, writeValue, ct);
        if (result.IsFailure)
        {
            _logger.LogWarning("写值失败: Device={DeviceId} Point={PointId} Value={Value} Error={Error}",
                request.DeviceId, request.PointId, writeValue, result.Error!.Message);
            return result.Error!;
        }

        _logger.LogInformation("写值成功: Device={DeviceId} Point={PointId} Value={Value}",
            request.DeviceId, request.PointId, writeValue);
        return OperationResult.Success();
    }

    /// <summary>
    /// 组合 WriteGuard 校验：数值/Bool 走三级门控（范围 + 变化率 + 在线），String 只做在线校验。
    /// 范围/变化率基于「工程值」（用户输入值）比较——点位 MinLimit/MaxLimit 与最近测量值均为工程单位。
    /// </summary>
    private OperationResult EvaluateGuard(
        Guid deviceId, DevicePoint point, object typedValue, string status, double? previousValue)
    {
        var cmd = new WriteCommand
        {
            DeviceId = deviceId,
            PointId = point.Id,
            DataType = point.DataType.ToString(),
            DeviceStatus = status,
            PreviousValue = previousValue,
            MinLimit = point.MinLimit,
            MaxLimit = point.MaxLimit
        };

        if (point.DataType == DataType.String)
        {
            // String：只校验设备在线（WriteCommand.Value 是 double，字符串无法参与范围/变化率校验）
            var mode = _mode.Validate(cmd with { Value = 0 });
            return mode.IsValid
                ? OperationResult.Success()
                : OperationalError.Validation(string.Join("；", mode.Errors.Select(e => e.ErrorMessage)));
        }

        // Bool 按 0/1 参与范围/变化率校验（Bool 点位范围通常配 0~1）
        var numeric = point.DataType == DataType.Bool
            ? ((bool)typedValue ? 1.0 : 0.0)
            : Convert.ToDouble(typedValue, CultureInfo.InvariantCulture);
        var result = _guard.Evaluate(cmd with { Value = numeric });
        return result.IsValid
            ? OperationResult.Success()
            : OperationalError.Validation(string.Join("；", result.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>
    /// 工程值 → 驱动写入值。String / Bool 不缩放；数值型默认 ScaleFactor=1/ScaleOffset=0 时恒等，
    /// 否则反算原始值 raw = (value − ScaleOffset) / ScaleFactor。
    /// </summary>
    private static object ToRawValue(DevicePoint point, object typedValue)
    {
        if (point.DataType is DataType.String or DataType.Bool)
            return typedValue;
        if (Math.Abs(point.ScaleFactor - 1.0) < 1e-12 && Math.Abs(point.ScaleOffset) < 1e-12)
            return typedValue;

        var value = Convert.ToDouble(typedValue, CultureInfo.InvariantCulture);
        if (Math.Abs(point.ScaleFactor) < 1e-12)
            throw new InvalidOperationException($"点位「{point.Name}」ScaleFactor 为 0，无法反算原始值");
        return (value - point.ScaleOffset) / point.ScaleFactor;
    }

    /// <summary>按点位 DataType 把用户输入（JsonElement/字符串/number/bool）转换为强类型值。</summary>
    private static OperationResult<object> ConvertValue(DataType type, object value)
    {
        try
        {
            var raw = Unwrap(value);
            object typed = type switch
            {
                DataType.Bool   => ToBool(raw),
                DataType.Byte   => Convert.ToByte(raw, CultureInfo.InvariantCulture),
                DataType.Int16  => Convert.ToInt16(raw, CultureInfo.InvariantCulture),
                DataType.UInt16 => Convert.ToUInt16(raw, CultureInfo.InvariantCulture),
                DataType.Int32  => Convert.ToInt32(raw, CultureInfo.InvariantCulture),
                DataType.UInt32 => Convert.ToUInt32(raw, CultureInfo.InvariantCulture),
                DataType.Int64  => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                DataType.UInt64 => Convert.ToUInt64(raw, CultureInfo.InvariantCulture),
                DataType.Float  => Convert.ToSingle(raw, CultureInfo.InvariantCulture),
                DataType.Double => Convert.ToDouble(raw, CultureInfo.InvariantCulture),
                DataType.String => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
                _               => Convert.ToDouble(raw, CultureInfo.InvariantCulture)
            };
            return OperationResult<object>.Success(typed);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return OperationalError.Validation($"值「{value}」无法转换为点位类型 {type}：{ex.Message}");
        }
    }

    /// <summary>把 JsonElement 解包为 CLR 原始值（number → long/double），其余原样返回。</summary>
    private static object? Unwrap(object? value)
    {
        if (value is not JsonElement je)
            return value;
        return je.ValueKind switch
        {
            JsonValueKind.Null      => null,
            JsonValueKind.String    => je.GetString(),
            JsonValueKind.Number    => je.TryGetInt64(out var l) ? l : je.GetDouble(),
            JsonValueKind.True      => true,
            JsonValueKind.False     => false,
            _                       => je.GetRawText()
        };
    }

    /// <summary>宽松布尔解析：支持 bool、0/1、true/false、ON/OFF（含大小写变体）。</summary>
    private static bool ToBool(object? value) => value switch
    {
        bool b   => b,
        byte by  => by != 0,
        short s  => s != 0,
        ushort us => us != 0,
        int i    => i != 0,
        uint ui  => ui != 0,
        long l   => l != 0,
        ulong ul => ul != 0,
        float f  => f != 0,
        double d => d != 0,
        decimal m => m != 0,
        string s => s switch
        {
            "1" or "true" or "True" or "TRUE" or "ON" or "On" or "on" => true,
            "0" or "false" or "False" or "FALSE" or "OFF" or "Off" or "off" => false,
            _ => throw new FormatException($"无法识别布尔值：{s}")
        },
        null => throw new InvalidCastException("布尔点位缺少值"),
        _ => throw new InvalidCastException($"无法转换为布尔值：{value}")
    };

    /// <summary>尽力把测量值转 double（供变化率校验；非数值/NaN/Infinity 返回 null）。</summary>
    private static double? TryToDouble(object? value)
    {
        if (value is null)
            return null;
        try
        {
            if (value is IConvertible convertible)
            {
                var d = convertible.ToDouble(CultureInfo.InvariantCulture);
                return double.IsNaN(d) || double.IsInfinity(d) ? null : d;
            }
        }
        catch
        {
            // 非数值点位（如字符串）不参与变化率校验
        }
        return null;
    }
}
