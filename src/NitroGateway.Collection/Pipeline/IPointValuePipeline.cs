using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

namespace NitroGateway.Collection;

/// <summary>值转换管道：RawPointValue → PointSnapshot</summary>
public interface IPointValuePipeline
{
    /// <summary>处理一批原始值，死区丢弃的不包含在结果中</summary>
    IReadOnlyList<PointSnapshot> Process(
        Guid deviceId, IReadOnlyList<RawPointValue> rawValues);

    /// <summary>获取上次工程值</summary>
    double? GetLastValue(Guid pointId);

    /// <summary>更新上次工程值缓存</summary>
    void SetLastValue(Guid pointId, double value);
}
