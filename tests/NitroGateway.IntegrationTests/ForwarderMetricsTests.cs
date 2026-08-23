using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using NitroGateway.Telemetry;
using Xunit;
using ForwarderImpl = NitroGateway.Forwarder.Forwarder;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// Forwarder 指标刷新测试（ADR-017 P2-1）：
/// BufferBacklog gauge 在空轮也必须刷新为 0，不能停在最后一个非零值；
/// 有积压时按缓冲实际剩余数刷新（并验证改走 GetCountAsync，P3-1）。
/// 本类与其它 Forwarder 测试同处 "Forwarder" 串行集合（见 ForwarderCollection）。
/// </summary>
[Collection("Forwarder")]
public class ForwarderMetricsTests
{
    private static ForwarderImpl CreateForwarder(FakeForwardBuffer buffer, FakeMqttClient mqtt)
        => new(buffer, new JsonMessageSerializer(), mqtt, NullLogger<ForwarderImpl>.Instance);

    [Fact]
    public async Task EmptyRound_ResetsBacklogGaugeToZero()
    {
        NitroMetrics.BufferBacklog.Set(42); // 模拟上一轮遗留的旧值
        var forwarder = CreateForwarder(new FakeForwardBuffer(), new FakeMqttClient());

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(0, NitroMetrics.BufferBacklog.Value);
    }

    [Fact]
    public async Task RoundWithPendingFailure_UpdatesGaugeToRemainingCount()
    {
        var buffer = new FakeForwardBuffer();
        await buffer.EnqueueAsync(new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() });
        var mqtt = new FakeMqttClient
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("broker 不可达"))
        };
        var forwarder = CreateForwarder(buffer, mqtt);

        await forwarder.ForwardBatchAsync(10);

        // 发布失败 → 批次经 MarkFailed 回 Pending，仍在缓冲 → gauge 反映剩余 1 批
        Assert.Equal(1, NitroMetrics.BufferBacklog.Value);
    }

    [Fact]
    public async Task RoundWithSuccess_CommitsAndGaugeReflectsEmptyBuffer()
    {
        var buffer = new FakeForwardBuffer();
        await buffer.EnqueueAsync(new BatchMeasurements { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() });
        var forwarder = CreateForwarder(buffer, new FakeMqttClient());

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(0, NitroMetrics.BufferBacklog.Value);
    }
}
