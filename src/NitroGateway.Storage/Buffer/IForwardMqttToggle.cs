using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

public interface IForwardMqttToggle
{
    /// <summary>
    /// 当前是否启用 MQTT 上云转发。热路径同步读取，实现必须返回内存缓存值；缺省视为启用（true）。
    /// </summary>
    bool IsEnabled { get; }

    event Action<bool>? EnabledChanged;

    /// <summary>
    /// 设置开关并持久化（重启保持）。持久化成功后才更新内存态；失败返回失败结果且内存态不变。
    /// </summary>
    /// <param name="enabled">是否启用 MQTT 上云转发</param>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> SetEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>
    /// 加载持久化状态到内存。宿主启动、迁移完成后调用一次；缺省或读取失败按启用处理（不阻断启动）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task<OperationResult> InitializeAsync(CancellationToken ct = default);
}
