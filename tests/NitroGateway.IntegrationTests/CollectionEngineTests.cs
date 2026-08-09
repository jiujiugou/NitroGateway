using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Host;
using Xunit;

namespace NitroGateway.UnitTests;

public class CollectionEngineTests
{
    /// <summary>前 N 轮抛异常的采集桩，之后正常</summary>
    private sealed class FlakyCollector : IDeviceCollector
    {
        public int Calls { get; private set; }
        public int FailuresRemaining { get; set; } = 1;

        public Task CollectDeviceAsync(Device device, CancellationToken ct)
        {
            Calls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("采集器故障");
            }
            return Task.CompletedTask;
        }

        public Task CollectOnceAsync(CancellationToken ct)
        {
            Calls++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("采集器故障");
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RoundFailure_DoesNotStopEngine_RetriesNextRound()
    {
        var collector = new FlakyCollector();
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceCollector>(collector);
        await using var provider = services.BuildServiceProvider();

        var engine = new CollectionEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GatewayLifecycle(),
            Options.Create(new CollectionOption { IntervalMs = 30 }),
            NullLogger<CollectionEngine>.Instance,
            TimeSpan.FromMilliseconds(50));

        await engine.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (collector.Calls < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(collector.Calls >= 2, $"期望异常后继续下一轮，实际 {collector.Calls} 轮");
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }
}
