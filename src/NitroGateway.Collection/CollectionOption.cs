namespace NitroGateway.Collection;

/// <summary>采集模块配置项，读取自配置节 <see cref="SectionName"/>（默认值见各属性）。</summary>
public sealed class CollectionOption
{
    /// <summary>配置节名称，对应 appsettings.json 的 "Collection" 节点。</summary>
    public const string SectionName = "Collection";

    /// <summary>采集间隔（毫秒），由 <see cref="CollectionEngine"/> 的 PeriodicTimer 使用；默认 1000。</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>单轮内并发采集的设备数上限；默认 5，由 DeviceCollector 信号量限流。</summary>
    public int MaxConcurrency { get; init; } = 5;

    /// <summary>熔断起步冷却秒数；默认 5，探测失败按指数翻倍至 <see cref="CircuitBreakerMaxOpenSeconds"/> 封顶。</summary>
    public int CircuitBreakerOpenSeconds { get; init; } = 5;

    /// <summary>熔断冷却封顶秒数；默认 300（5 分钟），防止长期熔断拖死采集。</summary>
    public int CircuitBreakerMaxOpenSeconds { get; init; } = 300;

}
