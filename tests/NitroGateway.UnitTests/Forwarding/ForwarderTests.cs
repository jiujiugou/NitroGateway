using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Measurements;
using NitroGateway.Forwarder;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Telemetry;
using NitroGateway.Transport.MQTT;
using Xunit;
using ForwarderImpl = NitroGateway.Forwarder.Forwarder;

namespace NitroGateway.UnitTests.Forwarding;

/// <summary>
/// Forwarder.ForwardBatchAsync 的核心行为：站点 topic、限流取数、提交/标记失败、
/// 串行异常与取消语义、积压 gauge 更新。与 Activity 测试同集合串行（共享全局指标/监听器）。
/// </summary>
[Collection(ActivityListenerCollection.Name)]
public class ForwarderTests
{
    private sealed class RecordingBuffer : IForwardBuffer
    {
        public List<BatchMeasurements> Pending { get; } = [];
        public List<Guid> Committed { get; } = [];
        // 并发发布下 MarkFailed 由多个 worker 触发，容器必须线程安全
        public ConcurrentBag<(Guid BatchId, string Reason)> MarkedFailed { get; } = [];
        public OperationalError? DequeueError { get; set; }
        public OperationalError? CommitError { get; set; }
        public OperationalError? MarkFailedError { get; set; }
        public int DequeueCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int LastMaxCount { get; private set; }

        public int Count => Pending.Count;

        public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(Pending.Count);

        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        {
            Pending.Add(batch);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
        {
            DequeueCalls++;
            LastMaxCount = maxCount;
            return Task.FromResult(DequeueError is not null
                ? OperationResult<IReadOnlyList<BatchMeasurements>>.Failure(DequeueError)
                : OperationResult<IReadOnlyList<BatchMeasurements>>.Success(Pending.Take(maxCount).ToList()));
        }

        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
        {
            CommitCalls++;
            if (CommitError is not null)
                return Task.FromResult(OperationResult.Failure(CommitError));
            Pending.RemoveAll(b => batchIds.Contains(b.Id));
            Committed.AddRange(batchIds);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
        {
            MarkedFailed.Add((batchId, reason));
            return Task.FromResult(MarkFailedError is not null
                ? OperationResult.Failure(MarkFailedError)
                : OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<DeadLetterEntry>>.Success([]));
        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    }

    private sealed class RecordingMqtt : IMqttClient
    {
        public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
        // 并发发布下 Published 由多个 worker 写入，容器必须线程安全
        public ConcurrentBag<(string Topic, byte[] Payload)> Published { get; } = [];
        public OperationResult? PublishResult { get; set; }
        public Exception? PublishException { get; set; }

        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
        {
            if (PublishException is not null)
                throw PublishException;
            if (PublishResult is not null)
                return Task.FromResult(PublishResult);
            Published.Add((topic, payload));
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";
        public byte[] Serialize(BatchMeasurements batch) => throw new InvalidOperationException("serialize boom");
    }

    private static BatchMeasurements Batch() => new() { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid() };

    private static ForwarderImpl Create(
        IForwardBuffer buffer, IMqttClient mqtt,
        string? siteId = null, IMessageSerializer? serializer = null,
        int? maxConcurrentPublishes = null)
        => new(buffer, serializer ?? new JsonMessageSerializer(), mqtt,
               NullLogger<ForwarderImpl>.Instance, siteId, maxConcurrentPublishes ?? 8);

    // ── 站点 topic（ADR-035）──

    [Fact]
    public async Task ForwardBatchAsync_PublishesToSiteTopic()
    {
        var buffer = new RecordingBuffer();
        var batch = Batch();
        await buffer.EnqueueAsync(batch);
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt, siteId: "plant-1");

        await forwarder.ForwardBatchAsync(10);

        var (topic, _) = Assert.Single(mqtt.Published);
        Assert.Equal($"nitrogateway/plant-1/{batch.DeviceId}/measurements", topic);
    }

    [Fact]
    public async Task ForwardBatchAsync_WhitespaceSiteId_UsesDefault()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt, siteId: "   ");

        await forwarder.ForwardBatchAsync(10);

        var (topic, _) = Assert.Single(mqtt.Published);
        Assert.StartsWith($"nitrogateway/{SiteOptions.DefaultSiteId}/", topic);
    }

    [Fact]
    public async Task ForwardBatchAsync_SiteId_IsTrimmed()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt, siteId: "  plant-1  ");

        await forwarder.ForwardBatchAsync(10);

        var (topic, _) = Assert.Single(mqtt.Published);
        Assert.StartsWith("nitrogateway/plant-1/", topic);
    }

    // ── 限流取数 ──

    /// <summary>maxCount 小于固定上限时按 maxCount 出队，不放大到 1000。</summary>
    [Fact]
    public async Task ForwardBatchAsync_RespectsMaxCount()
    {
        var buffer = new RecordingBuffer();
        for (var i = 0; i < 3; i++)
            await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt);

        await forwarder.ForwardBatchAsync(1);

        Assert.Single(mqtt.Published);
        Assert.Single(buffer.Committed);
        Assert.Equal(2, buffer.Pending.Count);
        Assert.Equal(1, buffer.LastMaxCount);
    }

    // ── 提交 / 失败标记 ──

    [Fact]
    public async Task ForwardBatchAsync_Success_CommitsAndUpdatesBacklog()
    {
        var buffer = new RecordingBuffer();
        var batch = Batch();
        await buffer.EnqueueAsync(batch);
        var forwarder = Create(buffer, new RecordingMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Equal(batch.Id, Assert.Single(buffer.Committed));
        Assert.Empty(buffer.Pending);
        Assert.Equal(1, buffer.CommitCalls);
        Assert.Equal(0, NitroMetrics.BufferBacklog.Value);
    }

    [Fact]
    public async Task ForwardBatchAsync_PublishFailure_MarksFailedAndDoesNotCommit()
    {
        var buffer = new RecordingBuffer();
        var batch = Batch();
        await buffer.EnqueueAsync(batch);
        var mqtt = new RecordingMqtt
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("broker 不可达"))
        };
        var forwarder = Create(buffer, mqtt);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(buffer.Committed);
        Assert.Equal(0, buffer.CommitCalls);
        var (id, reason) = Assert.Single(buffer.MarkedFailed);
        Assert.Equal(batch.Id, id);
        Assert.Contains("broker 不可达", reason);
        Assert.Equal(1, NitroMetrics.BufferBacklog.Value);
    }

    /// <summary>部分批次成功：已成功批次仍应提交，失败批次标记。</summary>
    [Fact]
    public async Task ForwardBatchAsync_PartialFailure_CommitsSuccessfulBatches()
    {
        var buffer = new RecordingBuffer();
        var ok = Batch();
        var bad = Batch();
        await buffer.EnqueueAsync(ok);
        await buffer.EnqueueAsync(bad);
        var mqtt = new TogglingMqtt(failTopicDeviceId: bad.DeviceId);
        var forwarder = Create(buffer, mqtt);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Equal(ok.Id, Assert.Single(buffer.Committed));
        Assert.Equal(bad.Id, Assert.Single(buffer.MarkedFailed).BatchId);
    }

    [Fact]
    public async Task ForwardBatchAsync_CommitFailure_StillReturnsSuccess()
    {
        var buffer = new RecordingBuffer { CommitError = OperationalError.Storage("commit boom") };
        await buffer.EnqueueAsync(Batch());
        var forwarder = Create(buffer, new RecordingMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, buffer.CommitCalls);
    }

    [Fact]
    public async Task ForwardBatchAsync_MarkFailedFailure_DoesNotThrow()
    {
        var buffer = new RecordingBuffer { MarkFailedError = OperationalError.Storage("mark boom") };
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("x"))
        };
        var forwarder = Create(buffer, mqtt);

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
    }

    // ── 异常 / 取消 ──

    [Fact]
    public async Task ForwardBatchAsync_SerializeThrows_MarksFailedWithoutPublishing()
    {
        var buffer = new RecordingBuffer();
        var batch = Batch();
        await buffer.EnqueueAsync(batch);
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt, serializer: new ThrowingSerializer());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Empty(mqtt.Published);
        Assert.Equal(batch.Id, Assert.Single(buffer.MarkedFailed).BatchId);
        Assert.Equal(0, buffer.CommitCalls);
    }

    /// <summary>驱动抛 OperationCanceledException 时上抛给引擎停机路径，不标记失败。</summary>
    [Fact]
    public async Task ForwardBatchAsync_OperationCanceled_Rethrows()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt { PublishException = new OperationCanceledException() };
        var forwarder = Create(buffer, mqtt);

        await Assert.ThrowsAsync<OperationCanceledException>(() => forwarder.ForwardBatchAsync(10));

        Assert.Empty(buffer.MarkedFailed);
        Assert.Empty(buffer.Committed);
    }

    /// <summary>停机取消后即使已发布成功也不提交（留待下次启动续传）。</summary>
    [Fact]
    public async Task ForwardBatchAsync_CancelledBeforeCommit_DoesNotCommit()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt();
        var forwarder = Create(buffer, mqtt);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await forwarder.ForwardBatchAsync(10, cts.Token);

        Assert.Equal(0, buffer.CommitCalls);
        Assert.Empty(buffer.Committed);
    }

    // ── 空队列 / 出队失败 ──

    [Fact]
    public async Task ForwardBatchAsync_EmptyQueue_ResetsBacklogGauge()
    {
        NitroMetrics.BufferBacklog.Set(42);
        var forwarder = Create(new RecordingBuffer(), new RecordingMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, NitroMetrics.BufferBacklog.Value);
    }

    [Fact]
    public async Task ForwardBatchAsync_DequeueFailure_ReturnsFailure()
    {
        var buffer = new RecordingBuffer { DequeueError = OperationalError.Storage("出队失败") };
        var forwarder = Create(buffer, new RecordingMqtt());

        var result = await forwarder.ForwardBatchAsync(10);

        Assert.True(result.IsFailure);
        Assert.Contains("出队失败", result.Error!.Message);
    }

    // ── 转发计数指标 ──

    [Fact]
    public async Task ForwardBatchAsync_Success_IncrementsSuccessCounter()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var forwarder = Create(buffer, new RecordingMqtt());
        var before = NitroMetrics.ForwardTotal.WithLabels("success").Value;

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(before + 1, NitroMetrics.ForwardTotal.WithLabels("success").Value);
    }

    [Fact]
    public async Task ForwardBatchAsync_PublishFailure_IncrementsFailureCounter()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var mqtt = new RecordingMqtt
        {
            PublishResult = OperationResult.Failure(OperationalError.Communication("x"))
        };
        var forwarder = Create(buffer, mqtt);
        var before = NitroMetrics.ForwardTotal.WithLabels("failure").Value;

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(before + 1, NitroMetrics.ForwardTotal.WithLabels("failure").Value);
    }

    [Fact]
    public async Task ForwardBatchAsync_SerializeThrows_IncrementsFailureCounter()
    {
        var buffer = new RecordingBuffer();
        await buffer.EnqueueAsync(Batch());
        var forwarder = Create(buffer, new RecordingMqtt(), serializer: new ThrowingSerializer());
        var before = NitroMetrics.ForwardTotal.WithLabels("failure").Value;

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(before + 1, NitroMetrics.ForwardTotal.WithLabels("failure").Value);
    }

    // ── 有界并发发布 ──

    /// <summary>并发发布受 MaxConcurrentPublishes 限制，单轮在途不超过配置值，且确实大于 1。</summary>
    [Fact]
    public async Task ForwardBatchAsync_BoundedConcurrency_NeverExceedsConfiguredDegree()
    {
        var buffer = new RecordingBuffer();
        for (var i = 0; i < 30; i++)
            await buffer.EnqueueAsync(Batch());
        var mqtt = new ConcurrencyTrackingMqtt { PublishDelay = TimeSpan.FromMilliseconds(30) };
        var forwarder = Create(buffer, mqtt, maxConcurrentPublishes: 4);

        await forwarder.ForwardBatchAsync(30);

        Assert.Equal(30, buffer.Committed.Count);
        Assert.Equal(30, mqtt.PublishedCount);
        Assert.InRange(mqtt.MaxObserved, 2, 4);
    }

    /// <summary>MaxConcurrentPublishes=1 退化为串行：在途峰值恒为 1。</summary>
    [Fact]
    public async Task ForwardBatchAsync_MaxConcurrentPublishesOne_IsSerial()
    {
        var buffer = new RecordingBuffer();
        for (var i = 0; i < 10; i++)
            await buffer.EnqueueAsync(Batch());
        var mqtt = new ConcurrencyTrackingMqtt { PublishDelay = TimeSpan.FromMilliseconds(5) };
        var forwarder = Create(buffer, mqtt, maxConcurrentPublishes: 1);

        await forwarder.ForwardBatchAsync(10);

        Assert.Equal(10, buffer.Committed.Count);
        Assert.Equal(1, mqtt.MaxObserved);
    }

    /// <summary>并发下部分设备失败：成功的全部提交、失败的全部标记。</summary>
    [Fact]
    public async Task ForwardBatchAsync_ConcurrentPartialFailure_CommitsSuccessesMarksFailures()
    {
        var buffer = new RecordingBuffer();
        var all = Enumerable.Range(0, 20).Select(_ => Batch()).ToList();
        var failing = all.Where((_, i) => i % 2 == 0).Select(b => b.DeviceId).ToHashSet();
        foreach (var b in all)
            await buffer.EnqueueAsync(b);
        var mqtt = new SetFailingMqtt(failing, TimeSpan.FromMilliseconds(10));
        var forwarder = Create(buffer, mqtt, maxConcurrentPublishes: 4);

        await forwarder.ForwardBatchAsync(20);

        var devOf = all.ToDictionary(b => b.Id, b => b.DeviceId);
        Assert.Equal(10, buffer.Committed.Count);
        Assert.All(buffer.Committed, id => Assert.False(failing.Contains(devOf[id])));
        Assert.Equal(10, buffer.MarkedFailed.Count);
        Assert.All(buffer.MarkedFailed, mf => Assert.True(failing.Contains(devOf[mf.BatchId])));
    }

    /// <summary>同一批只发布一次（并发不产生重复发送）。</summary>
    [Fact]
    public async Task ForwardBatchAsync_Concurrent_DoesNotPublishDuplicateBatches()
    {
        var buffer = new RecordingBuffer();
        var all = Enumerable.Range(0, 50).Select(_ => Batch()).ToList();
        foreach (var b in all)
            await buffer.EnqueueAsync(b);
        var mqtt = new ConcurrencyTrackingMqtt { PublishDelay = TimeSpan.FromMilliseconds(2) };
        var forwarder = Create(buffer, mqtt, maxConcurrentPublishes: 8);

        await forwarder.ForwardBatchAsync(50);

        // 每批 DeviceId 唯一 ⇒ topic 唯一；重复发布会出现重复 topic
        Assert.Equal(50, mqtt.PublishedTopics.Count);
        Assert.Equal(50, mqtt.PublishedTopics.Distinct().Count());
    }

    /// <summary>并发发布时若配置越界应被夹紧而非抛异常（0 → 串行，100 → 64）。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100, 64)]
    public void Constructor_ClampsMaxConcurrentPublishes(int configured, int expected)
    {
        var forwarder = Create(new RecordingBuffer(), new RecordingMqtt(),
            maxConcurrentPublishes: configured);
        var actual = (int)typeof(ForwarderImpl)
            .GetField("_maxConcurrentPublishes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(forwarder)!;

        Assert.Equal(expected, actual);
    }

    /// <summary>按 deviceId 失败特定 topic 的 MQTT 替身（构造部分成功场景）。</summary>
    private sealed class TogglingMqtt(Guid failTopicDeviceId) : IMqttClient
    {
        public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(topic.Contains(failTopicDeviceId.ToString())
                ? OperationResult.Failure(OperationalError.Communication("broker 不可达"))
                : OperationResult.Success());

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>记录在途并发峰值的 MQTT 替身：发布前自增、延时、发布后自减，用于验证并发上限。</summary>
    private sealed class ConcurrencyTrackingMqtt : IMqttClient
    {
        private int _current;
        private int _max;

        public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;
        public int MaxObserved => Volatile.Read(ref _max);
        public int PublishedCount => PublishedTopics.Count;
        public ConcurrentBag<string> PublishedTopics { get; } = [];

        public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());

        public async Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _current);
            UpdateMax(now);
            try
            {
                if (PublishDelay > TimeSpan.Zero)
                    await Task.Delay(PublishDelay, ct).ConfigureAwait(false);
                PublishedTopics.Add(topic);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
            return OperationResult.Success();
        }

        private void UpdateMax(int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref _max)))
            {
                if (Interlocked.CompareExchange(ref _max, value, seen) == seen)
                    break;
            }
        }

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>按 deviceId 集合失败特定 topic 的 MQTT 替身；带延时以制造真实并发。</summary>
    private sealed class SetFailingMqtt(IReadOnlySet<Guid> failingDeviceIds, TimeSpan delay) : IMqttClient
    {
        public MqttConnectionState State { get; set; } = MqttConnectionState.Connected;
        public event Action<MqttConnectionState>? StateChanged;

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());

        public async Task<OperationResult> PublishAsync(string topic, byte[] payload, int qos = 1, CancellationToken ct = default)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct).ConfigureAwait(false);
            var failed = failingDeviceIds.Any(id => topic.Contains(id.ToString()));
            return failed
                ? OperationResult.Failure(OperationalError.Communication("broker 不可达"))
                : OperationResult.Success();
        }

        public Task<OperationResult> SubscribeAsync(string topic, int qos = 1, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public IAsyncEnumerable<MqttMessage> Messages => EmptyMessages();

        private static async IAsyncEnumerable<MqttMessage> EmptyMessages()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
