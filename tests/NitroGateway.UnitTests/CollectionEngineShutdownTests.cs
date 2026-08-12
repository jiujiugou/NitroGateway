using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Host;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 采集引擎关闭路径：StopAsync 取消停止令牌时，PeriodicTimer 等待抛出的
/// OperationCanceledException 必须在引擎内部吞掉，不得逃逸出 ExecuteAsync。
/// （逃逸时宿主虽会静默吞并，但调试器会显示 first-chance 异常，关闭路径也不干净。）
/// </summary>
public class CollectionEngineShutdownTests
{
    private sealed class NoopCollector : IDeviceCollector
    {
        public Task CollectDeviceAsync(Device device, CancellationToken ct) => Task.CompletedTask;

        public Task CollectOnceAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task StopWhileWaitingForNextTick_ExecuteTaskCompletesSuccessfully()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceCollector>(new NoopCollector());
        await using var provider = services.BuildServiceProvider();

        var engine = new CollectionEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GatewayLifecycle(),
            Options.Create(new CollectionOption { IntervalMs = 1000 }),
            NullLogger<CollectionEngine>.Instance);

        await engine.StartAsync(CancellationToken.None);

        // 引用必须在 StopAsync 前抓取（BackgroundService.StopAsync 结束时会置空 ExecuteTask）。
        var executeTask = engine.ExecuteTask;
        Assert.NotNull(executeTask);

        // 确保引擎已进入 WaitForNextTickAsync 等待，随后 StopAsync 取消停止令牌。
        await Task.Delay(100);
        await engine.StopAsync(CancellationToken.None);

        // 修复前：等待被取消 → ODE 逃逸 → ExecuteTask 为 Canceled；
        // 修复后：引擎内部吞掉关闭期 ODE → 正常成功完成。
        Assert.True(executeTask.IsCompletedSuccessfully,
            $"停止后 ExecuteTask 应为成功完成，实际 canceled={executeTask.IsCanceled}, faulted={executeTask.IsFaulted}");
    }
}
