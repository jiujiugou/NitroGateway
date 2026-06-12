using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 协议驱动统一接口。
/// 每种工业协议（Modbus、OPC UA、S7 等）提供一个实现，负责连接的建立/断开与点位的读写。
/// 调用方无需关心底层协议细节，通过本接口即可操作任意协议的设备。
/// 所有操作返回 <see cref="OperationResult"/>，不抛异常。
/// </summary>
public interface IProtocolDriver
{
    /// <summary>当前连接状态</summary>
    DriverState State { get; }

    /// <summary>驱动能力声明</summary>
    DriverCapability Capability { get; }

    /// <summary>建立设备连接</summary>
    Task<OperationResult> ConnectAsync(CancellationToken ct = default);

    /// <summary>断开设备连接</summary>
    Task<OperationResult> DisconnectAsync(CancellationToken ct = default);

    /// <summary>连接验证，发最小代价的读请求确认设备可达</summary>
    Task<OperationResult> PingAsync(CancellationToken ct = default);

    /// <summary>读取单个点位，返回原始数据（不做类型转换和缩放）</summary>
    /// <param name="point">点位定义（含地址、数据类型）</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default);

    /// <summary>批量读取多个点位。驱动不支持批量时，由调用方逐个调用 <see cref="ReadAsync"/></summary>
    /// <param name="points">点位定义集合</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default);

    /// <summary>向单个点位写入值</summary>
    /// <param name="point">点位定义</param>
    /// <param name="value">写入值，类型需与 <see cref="DevicePoint.DataType"/> 匹配</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default);

    /// <summary>批量写入多个点位。驱动不支持批量时，由调用方逐个调用 <see cref="WriteAsync"/></summary>
    /// <param name="entries">点位与值的键值对集合</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default);
}
