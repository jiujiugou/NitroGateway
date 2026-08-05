using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;
using Xunit;

namespace NitroGateway.IntegrationTests;

public class PipelineDispatchIntegrationTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("等待写入超时");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Pipeline_To_Dispatcher_WritesStoreAndBuffers()
    {
        var store = new FakeMeasurementStore();
        var buffer = new FakeForwardBuffer();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMeasurementStore>(store);
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IPointValuePipeline, PointValuePipeline>();
        services.AddSingleton<MeasurementWriteHost>();
        services.AddSingleton<SinkDispatcher>();
        services.AddSingleton<IDataDispatcher, DataDispatcher>();

        await using var provider = services.BuildServiceProvider();
        var writeHost = provider.GetRequiredService<MeasurementWriteHost>();
        var sink = provider.GetRequiredService<SinkDispatcher>();
        await writeHost.StartAsync(CancellationToken.None);
        await sink.StartAsync(CancellationToken.None);

        try
        {
            var pipeline = provider.GetRequiredService<IPointValuePipeline>();
            var dispatcher = provider.GetRequiredService<IDataDispatcher>();
            var deviceId = Guid.NewGuid();
            var pt = new DevicePoint
            {
                Id = Guid.NewGuid(),
                Name = "T1",
                Address = "40001",
                DataType = DataType.Float,
                ScaleFactor = 1.0
            };
            var raw = new RawPointValue { Point = pt, Value = 42.5d, Timestamp = DateTime.UtcNow };

            var snapshots = pipeline.Process(deviceId, [raw]);
            var result = await dispatcher.DispatchAsync(deviceId, snapshots, CancellationToken.None);

            Assert.True(result.IsSuccess);
            await WaitUntilAsync(() => store.Written.Count >= 1);
            Assert.Single(store.Written);
            Assert.Equal(42.5, (double)store.Written[0].Value!);
            Assert.True(buffer.Pending.Count >= 1, "转发缓冲应收到批次");
        }
        finally
        {
            await writeHost.StopAsync(CancellationToken.None);
            await sink.StopAsync(CancellationToken.None);
        }
    }
}