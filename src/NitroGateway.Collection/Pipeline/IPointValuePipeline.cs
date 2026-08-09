using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Collection;

/// <summary>
/// 值转换管道：RawPointValue → PointSnapshot（协议解码值 → 工程缩放 → 死区 → 快照）。
/// 纯计算，无 IO 副作用；唯一可变状态是内存中的"上次工程值"缓存（重启丢失）。
/// </summary>
public interface IPointValuePipeline
{
    /// <summary>
    /// 处理一批原始值，返回快照列表。
    /// </summary>
    /// <param name="deviceId">所属设备 ID（RawPointValue 不持有 DeviceId，由调用方传入）</param>
    /// <param name="rawValues">原始值列表</param>
    /// <returns>转换后的快照列表；缩放失败的点位以 Uncertain 质量保留（不丢弃）</returns>
    IReadOnlyList<PointSnapshot> Process(
        Guid deviceId, IReadOnlyList<RawPointValue> rawValues);

    /// <summary>
    /// 获取点位上次工程值（死区判定与告警 Duration 使用）。
    /// </summary>
    /// <param name="pointId">点位 ID</param>
    /// <returns>上次工程值；无历史值返回 null</returns>
    double? GetLastValue(Guid pointId);

    /// <summary>
    /// 更新点位上次工程值缓存。
    /// </summary>
    /// <param name="pointId">点位 ID</param>
    /// <param name="value">最新工程值</param>
    void SetLastValue(Guid pointId, double value);
}
