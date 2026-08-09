using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Forwarder;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>AddNitroForwarder 注册参数校验测试（ADR-017 P3-2）</summary>
public class ForwarderRegistrationTests
{
    /// <summary>非正数间隔启动即报错并指明字段，避免 PeriodicTimer 运行时抛晦涩异常</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void AddNitroForwarder_NonPositiveInterval_Throws(int intervalMs)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddNitroForwarder(intervalMs));

        Assert.Equal("intervalMs", ex.ParamName);
    }

    /// <summary>正数间隔正常注册转发器与序列化器</summary>
    [Fact]
    public void AddNitroForwarder_PositiveInterval_Registers()
    {
        var services = new ServiceCollection();

        services.AddNitroForwarder(1000);

        Assert.Contains(services, s => s.ServiceType == typeof(IForwarder));
        Assert.Contains(services, s => s.ServiceType == typeof(IMessageSerializer));
    }
}
