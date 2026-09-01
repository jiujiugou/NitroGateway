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
