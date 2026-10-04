using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NitroGateway.Forwarder;
using NitroGateway.Host;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.HTTP;
using Xunit;

namespace NitroGateway.UnitTests.Forwarding;

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

    /// <summary>配置驱动注册应把 Forwarder 节绑定到 ForwarderOption。</summary>
    [Fact]
    public void AddNitroForwarder_Config_BindsForwarderOption()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Forwarder:IntervalMs"] = "1234" })
            .Build());
        using var provider = services.BuildServiceProvider();

        var option = provider.GetRequiredService<IOptions<ForwarderOption>>().Value;

        Assert.Equal(1234, option.IntervalMs);
    }

    /// <summary>MaxConcurrentPublishes 应能从配置绑定。</summary>
    [Fact]
    public void AddNitroForwarder_Config_BindsMaxConcurrentPublishes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forwarder:IntervalMs"] = "5000",
                ["Forwarder:MaxConcurrentPublishes"] = "16"
            })
            .Build());
        using var provider = services.BuildServiceProvider();

        var option = provider.GetRequiredService<IOptions<ForwarderOption>>().Value;

        Assert.Equal(16, option.MaxConcurrentPublishes);
    }

    /// <summary>越界 MaxConcurrentPublishes 应在选项校验时失败。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    public void AddNitroForwarder_ConfigOutOfRangeConcurrency_OptionsValidationThrows(string value)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forwarder:MaxConcurrentPublishes"] = value
            })
            .Build());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ForwarderOption>>().Value);

        Assert.Contains("MaxConcurrentPublishes", ex.Message);
    }

    /// <summary>非正数 IntervalMs 在选项校验时失败，而非到运行期才抛晦涩异常。</summary>
    [Fact]
    public void AddNitroForwarder_ConfigZeroInterval_OptionsValidationThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Forwarder:IntervalMs"] = "0" })
            .Build());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ForwarderOption>>().Value);

        Assert.Contains("IntervalMs", ex.Message);
    }

    /// <summary>启用 http 通道但缺 BaseUrl 应在选项校验时失败。</summary>
    [Fact]
    public void AddNitroForwarder_ConfigHttpWithoutBaseUrl_OptionsValidationThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroForwarder(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Forwarder:Channels"] = "http" })
            .Build());
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ForwarderOption>>().Value);

        Assert.Contains("BaseUrl", ex.Message);
    }

    /// <summary>int 重载的 intervalMs 应传入引擎（非默认 5000）。</summary>
    [Fact]
    public void AddNitroForwarder_IntInterval_PassedToEngine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwardBuffer, StubForwardBuffer>();
        services.AddSingleton<GatewayLifecycle>();
        services.AddNitroForwarder(1234);
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetServices<IHostedService>().OfType<ForwarderEngine>().Single();
        var interval = (TimeSpan)typeof(ForwarderEngine)
            .GetField("_interval", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(engine)!;

        Assert.Equal(TimeSpan.FromMilliseconds(1234), interval);
    }
}
