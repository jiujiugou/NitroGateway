using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.HTTP;
using Xunit;

namespace NitroGateway.IntegrationTests;

/// <summary>
/// HTTP 北向通道引擎测试（ADR-011 P2）：fake IHttpClient 驱动——成功 → Commit、
/// 失败 → MarkFailed、断线 → 跳过本轮。缓冲用内存替身（FakeForwardBuffer），
/// 引擎按 HttpChannel 出队（接口默认实现委托，单通道场景等价）。
/// </summary>
[Collection("Forwarder")]
public class HttpForwarderEngineTests
{
    private sealed class FakeHttpClient : IHttpClient
    {
        public HttpConnectionState State { get; set; } = HttpConnectionState.Connected;

        public event Action<HttpConnectionState>? StateChanged;

        public List<(string Path, object? Payload)> Uploaded { get; } = [];

        public OperationResult UploadResult { get; set; } = OperationResult.Success();

        public Task<OperationResult<HttpResponse>> SendAsync(HttpRequest request, CancellationToken ct = default)
            => Task.FromResult(OperationResult<HttpResponse>.Success(new HttpResponse { StatusCode = 200 }));

        public Task<OperationResult> UploadAsync<T>(string path, T payload, CancellationToken ct = default)
        {
            Uploaded.Add((path, payload));
            return Task.FromResult(UploadResult);
        }

        public Task<OperationResult> HealthCheckAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(condition(), "等待条件超时");
    }

    /// <summary>成功上传 → 批次 Commit（从缓冲移除），不再 MarkFailed</summary>
    [Fact]
    public async Task UploadSuccess_CommitsBatch()
    {
        var buffer = new FakeForwardBuffer();
        var http = new FakeHttpClient { State = HttpConnectionState.Connected };
        var batch = NewBatch();
        await buffer.EnqueueAsync(batch, IForwardBuffer.HttpChannel);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IHttpClient>(http);
        await using var provider = services.BuildServiceProvider();

        var engine = new HttpForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(20),
            buffer,
            "/api/measurements/batch",
            provider.GetRequiredService<ILogger<HttpForwarderEngine>>());

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => http.Uploaded.Count > 0, TimeSpan.FromSeconds(5));
            Assert.Equal("/api/measurements/batch", http.Uploaded[0].Path);
            // 上传与提交在同一轮内先后完成，断言前等待提交落地，避免与引擎线程的调度竞态
            await WaitForAsync(() => buffer.Committed.Contains(batch.Id), TimeSpan.FromSeconds(5));
            Assert.Contains(buffer.Committed, id => id == batch.Id);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>上传失败 → MarkFailed（重试/死信语义与 MQTT 引擎一致），不 Commit</summary>
    [Fact]
    public async Task UploadFailure_MarksFailed()
    {
        var buffer = new FakeForwardBuffer();
        var http = new FakeHttpClient
        {
            State = HttpConnectionState.Connected,
            UploadResult = OperationResult.Failure(OperationalError.General("500 from cloud"))
        };
        var batch = NewBatch();
        await buffer.EnqueueAsync(batch, IForwardBuffer.HttpChannel);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IHttpClient>(http);
        await using var provider = services.BuildServiceProvider();

        var engine = new HttpForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(20),
            buffer,
            "/api/measurements/batch",
            provider.GetRequiredService<ILogger<HttpForwarderEngine>>());

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => buffer.MarkedFailed.Count > 0, TimeSpan.FromSeconds(5));
            Assert.Contains(buffer.MarkedFailed, m => m.BatchId == batch.Id);
            Assert.DoesNotContain(buffer.Committed, id => id == batch.Id);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>断线（Disconnected）→ 跳过本轮，不出队不上传（断线语义与 MQTT 引擎一致）</summary>
    [Fact]
    public async Task Disconnected_SkipsRound()
    {
        var buffer = new FakeForwardBuffer();
        var http = new FakeHttpClient { State = HttpConnectionState.Disconnected };
        var batch = NewBatch();
        await buffer.EnqueueAsync(batch, IForwardBuffer.HttpChannel);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwardBuffer>(buffer);
        services.AddSingleton<IHttpClient>(http);
        await using var provider = services.BuildServiceProvider();

        var engine = new HttpForwarderEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromMilliseconds(20),
            buffer,
            "/api/measurements/batch",
            provider.GetRequiredService<ILogger<HttpForwarderEngine>>());

        await engine.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            Assert.Empty(http.Uploaded);
            Assert.Empty(buffer.Committed);
            Assert.Empty(buffer.MarkedFailed);
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
        }
    }

    private static BatchMeasurements NewBatch()
    {
        return new BatchMeasurements
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
    }
}
