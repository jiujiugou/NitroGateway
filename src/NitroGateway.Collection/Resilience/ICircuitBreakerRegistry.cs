namespace NitroGateway.Collection;

/// <summary>
/// 熔断器注册表，管理所有设备的熔断器实例（按设备 ID 一一对应，互不影响）。
/// 实例惰性创建：设备首次被请求时才建立，此后复用。
/// </summary>
public interface ICircuitBreakerRegistry
{
    /// <summary>获取或创建指定设备的熔断器（线程安全）。</summary>
    /// <param name="deviceId">设备 ID</param>
    /// <returns>该设备的熔断器实例</returns>
    ICircuitBreaker Get(Guid deviceId);

    /// <summary>重置指定设备的熔断器；设备不存在时为无操作。</summary>
    /// <param name="deviceId">设备 ID</param>
    void Reset(Guid deviceId);

    /// <summary>获取所有已知熔断器及对应设备 ID 的只读快照。</summary>
    IReadOnlyDictionary<Guid, ICircuitBreaker> GetAll();
}
