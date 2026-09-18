using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Forwarder;
using NitroGateway.Transport.HTTP;
using Xunit;

namespace NitroGateway.UnitTests;

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

    [Fact]
    public void AddNitroForwarder_ConfigHttp_RegistersHttpEngineAndClient()
    {
        var services = new ServiceCollection();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forwarder:Channels"] = "http",
                ["Forwarder:Http:BaseUrl"] = "https://center.example.com"
            })
            .Build());

        Assert.Contains(services, s => s.ServiceType == typeof(IHttpClient));
        Assert.Contains(services, s => s.ImplementationFactory?.Method.ReturnType == typeof(HttpForwarderEngine));
        Assert.DoesNotContain(services, s => s.ImplementationFactory?.Method.ReturnType == typeof(ForwarderEngine));
    }

    [Fact]
    public void AddNitroForwarder_ConfigBoth_RegistersBothEngines()
    {
        var services = new ServiceCollection();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forwarder:Channels"] = "both",
                ["Forwarder:Http:BaseUrl"] = "https://center.example.com"
            })
            .Build());

        Assert.Contains(services, s => s.ImplementationFactory?.Method.ReturnType == typeof(ForwarderEngine));
        Assert.Contains(services, s => s.ImplementationFactory?.Method.ReturnType == typeof(HttpForwarderEngine));
        Assert.Contains(services, s => s.ServiceType == typeof(IHttpClient));
    }

    [Fact]
    public void AddNitroForwarder_InvalidChannels_Throws()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddNitroForwarder(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Forwarder:Channels"] = "carrier-pigeon" })
                .Build()));

        Assert.Contains("Channels", ex.Message);
    }
}
