using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Measurements;
using NitroGateway.Persistence.Sqlite;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>DeadLetterRetentionService 测试（ADR-018 P2-3）：周期清理阈值正确、失败不中断。</summary>
public class DeadLetterRetentionServiceTests
{
    /// <summary>记录每次 PurgeDeadLettersAsync 阈值，前 N 次可注入失败。</summary>
    private sealed class RecordingBuffer : IForwardBuffer
    {
        public List<DateTime> PurgeBefore { get; } = [];
        public int FailuresRemaining { get; set; }

        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return Task.FromResult(OperationResult.Failure(OperationalError.Storage("磁盘故障")));
            }
            PurgeBefore.Add(before);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<BatchMeasurements>>>(Array.Empty<BatchMeasurements>());
        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<DeadLetterEntry>>>(Array.Empty<DeadLetterEntry>());
        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public int Count => 0;
        public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task ExecuteAsync_PurgesPeriodically_WithRetentionThreshold()
    {
        var buffer = new RecordingBuffer();
        var service = new DeadLetterRetentionService(
            buffer,
            NullLogger<DeadLetterRetentionService>.Instance,
            retentionDays: 30,
            interval: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (buffer.PurgeBefore.Count < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(buffer.PurgeBefore.Count >= 2, "服务应按周期重复调用 PurgeDeadLettersAsync");
            var expected = DateTime.UtcNow.AddDays(-30);
            foreach (var before in buffer.PurgeBefore)
            {
                Assert.True((expected - before).Duration() < TimeSpan.FromMinutes(1), "清理阈值应约为 now-30 天");
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PurgeFailure_DoesNotStopService()
    {
        var buffer = new RecordingBuffer { FailuresRemaining = 1 };
        var service = new DeadLetterRetentionService(
            buffer,
            NullLogger<DeadLetterRetentionService>.Instance,
            retentionDays: 7,
            interval: TimeSpan.FromMilliseconds(50));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (buffer.PurgeBefore.Count < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.True(buffer.PurgeBefore.Count >= 1, "首次失败后下个周期应重试成功");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
