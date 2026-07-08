namespace NitroGateway.Domain.Events;

/// <summary>
/// 点位存储完成回调。由需要订阅存储事件的模块实现（Alarm、Statistics 等）。
/// 每个实现以 Singleton 注册，Dispatcher 遍历调用。
/// </summary>
public interface IPointStoredSink
{
    /// <summary>处理已存储的点位数据</summary>
    ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default);
}
