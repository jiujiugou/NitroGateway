using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using Xunit;
using ForwarderImpl = NitroGateway.Forwarder.Forwarder;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// Forwarder 失败路径可观测性测试（ADR-001 P1-3）：
/// Dequeue 失败必须返回失败结果并记 Error；Commit/MarkFailed 失败必须记 Error，
/// 不再静默吞掉，避免转发停滞无信号、批次卡 InFlight 无法发现。
/// </summary>
public class ForwarderFailureTests
{
    private static ForwarderImpl CreateForwarder(
        FakeForwardBuffer buffer, FakeMqttClient mqtt, CapturingLogger<ForwarderImpl> logger)
        => new(buffer, new JsonMessageSerializer(), mqtt, new ForwardingThrottle(), logger);

    private static BatchMeasurements NewBatch(Guid id) => new()
    {
        Id = id,
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

    /// <summary>P1-3①：Dequeue 失败不再静默返回 Success，而是返回失败结果</summary>
    [Fact]
    public async Task ForwardBatchAsync_DequeueFailure_ReturnsFailureResult()
    {
        var buffer = new FakeForwardBuffer { DequeueError = OperationalError.Storage("Buffer 出队失败") };
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(buffer, new FakeMqttClient(), logger);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Storage, result.Error!.Category);
    }

    /// <summary>P1-3①：Dequeue 失败必须记录 Error 级日志，转发停滞有信号</summary>
    [Fact]
    public async Task ForwardBatchAsync_DequeueFailure_LogsError()
    {
        var buffer = new FakeForwardBuffer { DequeueError = OperationalError.Storage("Buffer 出队失败") };
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(buffer, new FakeMqttClient(), logger);

        await forwarder.ForwardBatchAsync(10);

        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.Contains("出队", log.Message);
    }

    /// <summary>P1-3②：MarkFailed 失败必须记 Error，否则批次卡 InFlight 且无信号</summary>
    [Fact]
    public async Task ForwardBatchAsync_MarkFailedFailure_LogsError()
    {
        var buffer = new FakeForwardBuffer();
        await buffer.EnqueueAsync(NewBatch(Guid.NewGuid()));
        buffer.MarkFailedError = OperationalError.Storage("标记失败异常");
        var mqtt = new FakeMqttClient
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("broker 不可达"))
        };
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(buffer, mqtt, logger);

        await forwarder.ForwardBatchAsync(10);

        var markFailed = Assert.Single(logger.Entries, e => e.Message.Contains("标记"));
        Assert.Equal(LogLevel.Error, markFailed.Level);
        Assert.Contains("InFlight", markFailed.Message);
    }

    /// <summary>P1-3②：MarkFailed 成功时批次转 Pending 重试，且记录失败原因</summary>
    [Fact]
    public async Task ForwardBatchAsync_PublishFailure_MarksFailedForRetry()
    {
        var batch = NewBatch(Guid.NewGuid());
        var buffer = new FakeForwardBuffer();
        await buffer.EnqueueAsync(batch);
        var mqtt = new FakeMqttClient
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("broker 不可达"))
        };
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(buffer, mqtt, logger);

        await forwarder.ForwardBatchAsync(10);

        var marked = Assert.Single(buffer.MarkedFailed);
        Assert.Equal(batch.Id, marked.BatchId);
        Assert.Contains("broker", marked.Reason);
    }

    /// <summary>P1-3②：Commit 失败必须记 Error，否则已转发批次卡 InFlight 且无信号</summary>
    [Fact]
    public async Task ForwardBatchAsync_CommitFailure_LogsError()
    {
        var buffer = new FakeForwardBuffer();
        await buffer.EnqueueAsync(NewBatch(Guid.NewGuid()));
        buffer.CommitError = OperationalError.Storage("Buffer 提交失败");
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(buffer, new FakeMqttClient(), logger);

        await forwarder.ForwardBatchAsync(10);

        var commitFailed = Assert.Single(logger.Entries, e => e.Message.Contains("提交"));
        Assert.Equal(LogLevel.Error, commitFailed.Level);
    }

    /// <summary>空队列是正常状态，不记 Error 日志</summary>
    [Fact]
    public async Task ForwardBatchAsync_EmptyQueue_ReturnsSuccessWithoutLog()
    {
        var logger = new CapturingLogger<ForwarderImpl>();
        var forwarder = CreateForwarder(new FakeForwardBuffer(), new FakeMqttClient(), logger);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(logger.Entries);
    }
}
