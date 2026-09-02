using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Domain.Protocols;

/// <summary>
/// 可将设备点位以服务端订阅方式推送给采集管道的协议能力。
/// </summary>
/// <remarks>
/// 该接口只描述原始值来源；转换、死区和分发仍由 Collection 的既有 Pipeline/Dispatcher 负责。
/// 不支持订阅或订阅启动失败时，调用方必须保留轮询路径。
/// </remarks>
public interface ISubscriptionSource
{
    /// <summary>收到一批质量合格的原始点位值时触发。</summary>
    event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived;

    /// <summary>当前是否已有生效的服务端订阅。</summary>
    bool IsSubscriptionActive { get; }

    /// <summary>
    /// 确保指定点位的订阅已生效。
    /// </summary>
    /// <param name="points">启用的设备点位</param>
    /// <param name="publishingIntervalMs">服务端发布间隔</param>
    Task<OperationResult> EnsureSubscriptionAsync(
        IReadOnlyList<DevicePoint> points,
        int publishingIntervalMs,
        CancellationToken ct = default);

    /// <summary>停止当前订阅并释放服务端资源。</summary>
    Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default);
}
