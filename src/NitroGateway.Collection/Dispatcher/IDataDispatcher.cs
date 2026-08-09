using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.Collection;

/// <summary>
/// 数据分发：写时序库（经 Channel 异步落库）+ 入转发缓冲 + 推送存储事件，三者互不阻塞。
/// </summary>
public interface IDataDispatcher
{
    /// <summary>
    /// 分发一批点位快照。
    /// </summary>
    /// <param name="deviceId">所属设备 ID</param>
    /// <param name="snapshots">点位快照列表；空列表直接成功返回</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作结果；缓冲入队失败会记录但整体仍视为成功（不阻塞其他写入）</returns>
    Task<OperationResult> DispatchAsync(
        Guid deviceId, IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct);
}
