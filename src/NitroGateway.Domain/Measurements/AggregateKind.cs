namespace NitroGateway.Domain.Measurements;

/// <summary>
/// 聚合类型，用于时序数据的降采样和统计查询。
/// </summary>
public enum AggregateKind
{
    /// <summary>平均值</summary>
    Avg,

    /// <summary>最大值</summary>
    Max,

    /// <summary>最小值</summary>
    Min,

    /// <summary>求和</summary>
    Sum,

    /// <summary>计数</summary>
    Count,

    /// <summary>首值</summary>
    First,

    /// <summary>末值</summary>
    Last
}
