using Microsoft.Extensions.DependencyInjection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;
using System.Text;
using Xunit;

namespace NitroGateway.IntegrationTests;

public class ForwarderIntegrationTests
{
    [Fact]
    public async Task Forwarder_PublishesBatch_AndCommitsBuffer()
    {
        var buffer = new FakeForwardBuffer();
        var mqtt = new FakeMqttClient();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IMqttClient>(mqtt);
        services.AddNitroForwarder(1000);
        await using var provider = services.BuildServiceProvider();

        var forwarder = provider.GetRequiredService<IForwarder>();

        var batch = new BatchMeasurements
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            ScanStartedAt = DateTime.UtcNow.AddSeconds(-1),
            ScanCompletedAt = DateTime.UtcNow,
            Records =
            [
                new MeasurementRecord
                {
                    Id = Guid.NewGuid(),
                    DeviceId = Guid.NewGuid(),
                    DevicePointId = Guid.NewGuid(),
                    PointName = "T1",
                    Value = 36.6d,
                    DataType = DataType.Float,
                    Timestamp = DateTime.UtcNow,
                    ReceivedAt = DateTime.UtcNow,
                    Quality = QualityCode.Good
                }
            ]
        };
        await buffer.EnqueueAsync(batch);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Single(mqtt.Published);
        var payload = Encoding.UTF8.GetString(mqtt.Published[0].Payload);
        Assert.Contains("T1", payload);
        Assert.Empty(buffer.Pending);
        Assert.Contains(batch.Id, buffer.Committed);
    }
}