using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocol.Abstractions;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ReliableProtocolDriver 超时注入测试（ADR-019 P2-4）：
/// 管线超时由构造参数控制（生产取 DeviceConnection.RequestTimeoutMs），不再硬编码 3s。
/// </summary>
public class ReliableProtocolDriverTests
{
    /// <summary>可编程内层驱动：延迟/失败次数/是否响应取消均可注入。</summary>
    private sealed class FakeInner : IProtocolDriver
    {
        public DriverState State { get; set; } = DriverState.Connected;
        public DriverCapability Capability { get; } = new();
        public int ReadCalls { get; private set; }
        public TimeSpan ReadDelay { get; init; }
        public bool HonorCancellation { get; init; } = true;
        public int FailuresRemaining { get; set; }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue { Point = point, Value = 1f, Timestamp = DateTime.UtcNow }));

        public async Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
            IEnumerable<DevicePoint> points, CancellationToken ct = default)
        {
            ReadCalls++;
            if (ReadDelay > TimeSpan.Zero)
            {
                if (HonorCancellation)
                    await Task.Delay(ReadDelay, ct);
                else
                    await Task.Delay(ReadDelay);
            }
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return OperationResult<IReadOnlyList<RawPointValue>>.Failure(OperationalError.Protocol("设备无响应"));
            }
            return OperationResult<IReadOnlyList<RawPointValue>>.Success(new List<RawPointValue>());
        }

        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public void Dispose() { }
    }

    /// <summary>内层在注入超时内完成 → 成功</summary>
    [Fact]
    public async Task ReadBatchAsync_InnerSucceedsWithinInjectedTimeout_ReturnsSuccess()
    {
        var inner = new FakeInner { ReadDelay = TimeSpan.FromMilliseconds(50) };
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 0,
            retryDelay: TimeSpan.FromMilliseconds(1));

        var r = await driver.ReadBatchAsync([]);

        Assert.True(r.IsSuccess, r.Error?.Message);
    }

    /// <summary>内层超过注入超时（且响应取消）→ 在注入超时附近返回失败（而非等内层完成/硬编码 3s）</summary>
    [Fact]
    public async Task ReadBatchAsync_InnerSlowerThanInjectedTimeout_ReturnsFailure()
    {
        var inner = new FakeInner { ReadDelay = TimeSpan.FromSeconds(30), HonorCancellation = true };
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromMilliseconds(200),
            maxRetryAttempts: 0,
            retryDelay: TimeSpan.FromMilliseconds(1));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await driver.ReadBatchAsync([]);
        sw.Stop();

        Assert.True(r.IsFailure, "超过注入超时应返回失败");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"应在注入超时附近返回，实际 {sw.Elapsed}");
    }

    /// <summary>内层失败可重试：初始 1 次 + 重试后成功</summary>
    [Fact]
    public async Task ReadBatchAsync_InnerFailsThenRetries_ReturnsSuccess()
    {
        var inner = new FakeInner { FailuresRemaining = 2 };
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(1));

        var r = await driver.ReadBatchAsync([]);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(3, inner.ReadCalls);   // 初始 1 + 重试 2
    }

    // ── ADR-070 层次1：节点浏览经装饰器转发（复用长连接，不经 Polly）──

    /// <summary>内层不支持浏览（如 Modbus/S7）→ 装饰器返回明确失败，不抛异常</summary>
    [Fact]
    public async Task BrowseAsync_InnerNotBrowseable_ReturnsProtocolError()
    {
        var driver = new ReliableProtocolDriver(
            new FakeInner(),
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 0);

        var r = await driver.BrowseAsync("ns=2;i=5001");

        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error!.Code);
        Assert.Contains("不支持节点浏览", r.Error.Message);
    }

    /// <summary>内层支持浏览 → 透传 parentNodeId，返回内层结果（长连接复用）</summary>
    [Fact]
    public async Task BrowseAsync_InnerBrowseable_ForwardsToInner()
    {
        var inner = new FakeBrowseInner();
        var driver = new ReliableProtocolDriver(
            inner,
            NullLogger<ReliableProtocolDriver>.Instance,
            requestTimeout: TimeSpan.FromSeconds(5),
            maxRetryAttempts: 0);

        var r = await driver.BrowseAsync("ns=2;i=5001");

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal("ns=2;i=5001", inner.LastParent);
        var node = Assert.Single(r.Value!);
        Assert.Equal("Int32Var", node.Name);
    }

    // ── ADR-071：订阅（ISubscriptionSource）经装饰器透传 ──

    private static ReliableProtocolDriver CreateDriver(IProtocolDriver inner) => new(
        inner,
        NullLogger<ReliableProtocolDriver>.Instance,
        requestTimeout: TimeSpan.FromSeconds(5),
        maxRetryAttempts: 0);

    /// <summary>内层支持订阅 → 点位与发布间隔透传，返回内层成功结果。</summary>
    [Fact]
    public async Task EnsureSubscriptionAsync_InnerSupportsSubscription_ForwardsPointsAndInterval()
    {
        var inner = new FakeSubscriptionInner { IsSubscriptionActive = true };
        var driver = CreateDriver(inner);
        var point = new DevicePoint
        {
            Id = Guid.NewGuid(),
            Name = "T",
            Address = "ns=2;i=1001",
            DataType = DataType.Float
        };

        var r = await driver.EnsureSubscriptionAsync([point], 500);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(1, inner.EnsureCallCount);
        Assert.Equal(point, Assert.Single(inner.LastEnsurePoints!));
        Assert.Equal(500, inner.LastPublishingIntervalMs);
    }

    /// <summary>内层不支持订阅（非 ISubscriptionSource，如 Modbus/S7）→ 返回 ProtocolError，不抛异常。</summary>
    [Fact]
    public async Task EnsureSubscriptionAsync_InnerNotSubscriptionSource_ReturnsProtocolError()
    {
        var driver = CreateDriver(new FakeInner());

        var r = await driver.EnsureSubscriptionAsync([], 500);

        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error!.Code);
        Assert.Contains("不支持订阅采集", r.Error.Message);
    }

    /// <summary>内层未连接 → Ensure 前自动建连（与 ReadBatchAsync 自动建连语义一致）。</summary>
    [Fact]
    public async Task EnsureSubscriptionAsync_InnerDisconnected_AutoConnectsBeforeEnsure()
    {
        var inner = new FakeSubscriptionInner { State = DriverState.Disconnected };
        var driver = CreateDriver(inner);

        var r = await driver.EnsureSubscriptionAsync([], 500);

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(1, inner.ConnectCallCount);
        Assert.Equal(1, inner.EnsureCallCount);
    }

    /// <summary>ValuesReceived 事件 add/remove 透传至内层订阅源。</summary>
    [Fact]
    public void ValuesReceived_AddRemove_ForwardsToInner()
    {
        var inner = new FakeSubscriptionInner();
        var driver = CreateDriver(inner);
        Func<IReadOnlyList<RawPointValue>, Task> handler = _ => Task.CompletedTask;

        driver.ValuesReceived += handler;
        Assert.Equal(1, inner.HandlerCount);

        driver.ValuesReceived -= handler;
        Assert.Equal(0, inner.HandlerCount);
    }

    /// <summary>IsSubscriptionActive 透传内层激活状态。</summary>
    [Fact]
    public void IsSubscriptionActive_TransparentToInner()
    {
        var inner = new FakeSubscriptionInner { IsSubscriptionActive = true };
        var driver = CreateDriver(inner);
        Assert.True(driver.IsSubscriptionActive);
    }

    /// <summary>内层不支持订阅 → StopSubscriptionAsync 返回 ProtocolError。</summary>
    [Fact]
    public async Task StopSubscriptionAsync_InnerNotSubscriptionSource_ReturnsProtocolError()
    {
        var driver = CreateDriver(new FakeInner());

        var r = await driver.StopSubscriptionAsync();

        Assert.True(r.IsFailure);
        Assert.Equal("ProtocolError", r.Error!.Code);
    }

    /// <summary>内层支持订阅 → Stop 透传至内层。</summary>
    [Fact]
    public async Task StopSubscriptionAsync_InnerSupportsSubscription_ForwardsToInner()
    {
        var inner = new FakeSubscriptionInner();
        var driver = CreateDriver(inner);

        var r = await driver.StopSubscriptionAsync();

        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(1, inner.StopCallCount);
    }
}

/// <summary>支持浏览的内层驱动（ADR-070 装饰器转发测试用）</summary>
internal sealed class FakeBrowseInner : IProtocolDriver, IBrowseableDriver
{
    public string? LastParent { get; private set; }

    public DriverState State => DriverState.Connected;
    public DriverCapability Capability { get; } = new() { SupportsBrowse = true };
    public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("不支持")));
    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(Array.Empty<RawPointValue>());
    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public void Dispose() { }

    public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default)
    {
        LastParent = parentNodeId;
        return Task.FromResult(OperationResult<IReadOnlyList<BrowseNode>>.Success(new[]
        {
            new BrowseNode
            {
                NodeId = "ns=2;i=1001",
                Name = "Int32Var",
                TypeName = "Int32",
                IsVariable = true,
                Access = "ReadWrite"
            }
        }));
    }
}

/// <summary>支持订阅的内层驱动（ADR-071 装饰器透传测试用）。</summary>
internal sealed class FakeSubscriptionInner : IProtocolDriver, ISubscriptionSource
{
    public DriverState State { get; set; } = DriverState.Connected;
    public DriverCapability Capability { get; init; } = new() { SupportsSubscription = true };
    public bool IsSubscriptionActive { get; set; }
    public int ConnectCallCount { get; private set; }
    public int EnsureCallCount { get; private set; }
    public IReadOnlyList<DevicePoint>? LastEnsurePoints { get; private set; }
    public int? LastPublishingIntervalMs { get; private set; }
    public int StopCallCount { get; private set; }
    public int HandlerCount => ValuesReceived?.GetInvocationList().Length ?? 0;

    public event Func<IReadOnlyList<RawPointValue>, Task>? ValuesReceived;

    public Task<OperationResult> EnsureSubscriptionAsync(
        IReadOnlyList<DevicePoint> points, int publishingIntervalMs, CancellationToken ct = default)
    {
        EnsureCallCount++;
        LastEnsurePoints = points;
        LastPublishingIntervalMs = publishingIntervalMs;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> StopSubscriptionAsync(CancellationToken ct = default)
    {
        StopCallCount++;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
    {
        ConnectCallCount++;
        State = DriverState.Connected;
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        => Task.FromResult(OperationResult<RawPointValue>.Success(new RawPointValue
        {
            Point = point,
            Value = 1,
            Timestamp = DateTime.UtcNow
        }));
    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult<OperationResult<IReadOnlyList<RawPointValue>>>(Array.Empty<RawPointValue>());
    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public void Dispose() { }
}
