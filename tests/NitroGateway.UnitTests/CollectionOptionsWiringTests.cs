using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-014：AddNitroCollection 把 CollectionOption 全字段绑定进 DI，
/// 熔断器冷却时长与采集并发上限来自配置而非硬编码默认值。
/// </summary>
public class CollectionOptionsWiringTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroCollection(new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build());
        return services.BuildServiceProvider();
    }

    /// <summary>配置节的 4 个字段全部绑定进 IOptions&lt;CollectionOption&gt;</summary>
    [Fact]
    public void AddNitroCollection_BindsAllOptionFields()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:IntervalMs"] = "123",
            ["Collection:MaxConcurrency"] = "7",
            ["Collection:CircuitBreakerOpenSeconds"] = "9",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "1234"
        });

        var options = provider.GetRequiredService<IOptions<CollectionOption>>().Value;

        Assert.Equal(123, options.IntervalMs);
        Assert.Equal(7, options.MaxConcurrency);
        Assert.Equal(9, options.CircuitBreakerOpenSeconds);
        Assert.Equal(1234, options.CircuitBreakerMaxOpenSeconds);
    }

    /// <summary>
    /// 熔断器冷却时长来自配置：CircuitBreakerOpenSeconds=0 时 Trip 后立即进入半开（TryEnterProbe=true）；
    /// 若仍走硬编码 5s 默认值，Trip 后应保持 Open（TryEnterProbe=false），此用例即红绿对照。
    /// </summary>
    [Fact]
    public void AddNitroCollection_CircuitBreakerOpenDurationComesFromConfig()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "0",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "10"
        });

        var registry = provider.GetRequiredService<ICircuitBreakerRegistry>();
        var breaker = registry.Get(Guid.NewGuid());

        breaker.Trip();

        Assert.True(breaker.TryEnterProbe());
    }

    /// <summary>无 Collection 配置节点时所有字段使用 CollectionOption 默认值，不抛异常</summary>
    [Fact]
    public void AddNitroCollection_MissingSection_UsesDefaults()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var options = provider.GetRequiredService<IOptions<CollectionOption>>().Value;

        Assert.Equal(1000, options.IntervalMs);
        Assert.Equal(5, options.MaxConcurrency);
        Assert.Equal(5, options.CircuitBreakerOpenSeconds);
        Assert.Equal(300, options.CircuitBreakerMaxOpenSeconds);
    }

    /// <summary>ADR-016 P2-2：IntervalMs<=0 / MaxConcurrency<=0 非法配置，解析 IOptions 即抛，启动 fail-fast</summary>
    [Theory]
    [InlineData("IntervalMs", "0")]
    [InlineData("IntervalMs", "-1")]
    [InlineData("MaxConcurrency", "0")]
    public void AddNitroCollection_InvalidIntervalOrConcurrency_Throws(string key, string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [$"Collection:{key}"] = value
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
    }

    /// <summary>ADR-016 P2-2：CircuitBreakerMaxOpenSeconds 小于 OpenSeconds 时拒绝启动</summary>
    [Fact]
    public void AddNitroCollection_MaxOpenLessThanOpen_Throws()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Collection:CircuitBreakerOpenSeconds"] = "10",
            ["Collection:CircuitBreakerMaxOpenSeconds"] = "5"
        });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CollectionOption>>().Value);
    }
}
