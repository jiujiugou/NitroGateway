namespace NitroGateway.Collection;

/// <summary>熔断器注册表，管理所有设备的熔断器实例</summary>
public interface ICircuitBreakerRegistry
{
    /// <summary>获取或创建指定设备的熔断器</summary>
    ICircuitBreaker Get(Guid deviceId);

    /// <summary>重置指定设备的熔断器。如果不存在则无操作</summary>
    void Reset(Guid deviceId);
}
