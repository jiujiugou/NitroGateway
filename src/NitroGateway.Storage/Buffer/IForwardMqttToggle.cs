using NitroGateway.Shared;

namespace NitroGateway.Storage.Buffer;

/// <summary>
/// MQTT 上云转发总开关（ADR-059）。
/// <para><b>语义（决策 B + 只控 MQTT）：</b>关闭时采集照常、本地 SQLite 照常、告警/web/SignalR 不受影响，
/// 仅跳过 mqtt 通道的转发缓冲入队——无缓冲堆积、不触发死信；恢复后从关闭时刻起续传，不补发关闭期数据。
/// 若 <c>Forwarder:Channels</c> 含 http，http 通道不受本开关影响（开关仅作用于 mqtt 通道）。</para>
/// <para><b>双宿主实现（同一接口、两套存储）：</b>Webapi 宿主存 <c>app_meta</c>（SQLite，重启保持），
/// Desktop 宿主存 <c>desktop-settings.json</c>。缺省视为启用（true）。</para>
/// <para><b>实现约束：</b><see cref="IsEnabled"/> 供采集热路径（DataDispatcher 每轮入队）同步读取，
/// 实现必须内存缓存、不落库；持久化仅在 <see cref="SetEnabledAsync"/> / <see cref="InitializeAsync"/> 发生。</para>
/// </summary>
public interface IForwardMqttToggle
{
    /// <summary>
    /// 当前是否启用 MQTT 上云转发。热路径同步读取，实现必须返回内存缓存值；缺省视为启用（true）。
    /// </summary>
    bool IsEnabled { get; }

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
