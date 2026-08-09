namespace NitroGateway.Collection;

/// <summary>熔断器状态</summary>
public enum CircuitState
{
    /// <summary>正常通行</summary>
    Closed,
    /// <summary>断路，拒绝所有请求</summary>
    Open,
    /// <summary>半开探测，允许一个请求通过以验证恢复</summary>
    HalfOpen
}
