using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 协议驱动统一接口。
/// 每种工业协议（Modbus、OPC UA、S7 等）提供一个实现，负责连接的建立/断开与点位的读写。
/// 调用方无需关心底层协议细节，通过本接口即可操作任意协议的设备。
/// 所有操作返回 <see cref="OperationResult"/>，不抛异常。
/// <para><b>释放契约（双接口）：</b></para>
/// <list type="bullet">
/// <item><b>同步 <see cref="IDisposable.Dispose"/></b>：尽力而为、<b>不排水</b>——不等在途操作/闸门
/// （ADR-077），幂等、不抛；释放后的调用必须返回失败 <see cref="OperationResult"/>。</item>
/// <item><b>异步 <see cref="IAsyncDisposable.DisposeAsync"/></b>：优雅拆除——等本驱动闸门后再拆，
/// 不阻塞线程，因此不会触发 ADR-077 的宿主 UI 死锁；幂等、不抛。</item>
/// </list>
/// <para>调用方须把「同步释放与在途调用并发」导致的失败当作可恢复错误处理；需要优雅关停时用异步释放。</para>
/// </summary>
public interface IProtocolDriver : IDisposable, IAsyncDisposable
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

    /// <summary>
    /// 默认异步释放：未显式实现异步拆除的驱动（含测试替身）退化为同步 <see cref="Dispose"/>。
    /// 具体驱动应覆写为「等本驱动闸门」的优雅拆除。
    /// </summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
