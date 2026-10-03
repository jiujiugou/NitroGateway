using System.Collections.Concurrent;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// 假驱动：用计数追踪释放，能发现"重复释放"与"泄漏"（区别于布尔标志）。
/// 同步/异步释放各自计数并累加到 <see cref="ReleaseCount"/>。
/// </summary>
internal sealed class CountingDriver : IProtocolDriver
{
    private int _disposeCount;
    private int _disposeAsyncCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);
    public int ReleaseCount => DisposeCount + DisposeAsyncCount;

    public DriverState State => DriverState.Connected;
    public DriverCapability Capability => new();

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> PingAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        => throw new NotSupportedException("并发测试不使用单点读");
    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public void Dispose() => Interlocked.Increment(ref _disposeCount);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeAsyncCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>计数工厂：记录创建次数、持有全部驱动、可注入创建失败。</summary>
internal sealed class CountingFactory : IProtocolDriverFactory
{
    private readonly ConcurrentBag<CountingDriver> _drivers = new();
    private int _created;

    /// <summary>为 true 时 Create 抛异常（I5）。</summary>
    public bool ThrowOnCreate { get; set; }

    /// <summary>创建前钩子（用于制造受控窗口）。</summary>
    public Action? BeforeCreate { get; set; }

    public int CreatedCount => Volatile.Read(ref _created);
    public IReadOnlyCollection<CountingDriver> Drivers => _drivers;
    public int TotalReleases => _drivers.Sum(d => d.ReleaseCount);

    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        BeforeCreate?.Invoke();
        if (ThrowOnCreate)
            throw new InvalidOperationException("factory boom");

        var driver = new CountingDriver();
        _drivers.Add(driver);
        Interlocked.Increment(ref _created);
        return driver;
    }
}

internal static class TestData
{
    public static Device MakeDevice(string endpoint = "192.168.1.1:502") => new()
    {
        Id = Guid.NewGuid(),
        Name = "PLC",
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = endpoint }
    };
}

/// <summary>Dispose 会阻塞的驱动：进入时发信号，等待显式放行（用于 I6 验证释放不在临界区）。</summary>
internal sealed class BlockingDisposeDriver : IProtocolDriver
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public void Release() => _release.TrySetResult();

    public DriverState State => DriverState.Connected;
    public DriverCapability Capability => new();

    public Task<OperationResult> ConnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
    public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(
        IEnumerable<DevicePoint> points, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
    public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
    public Task<OperationResult> WriteBatchAsync(
        IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public void Dispose()
    {
        _entered.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }
}

/// <summary>创建 <see cref="BlockingDisposeDriver"/> 的工厂，暴露最近创建的实例。</summary>
internal sealed class BlockingDisposeFactory : IProtocolDriverFactory
{
    private BlockingDisposeDriver? _last;

    public BlockingDisposeDriver Last => _last!;

    public IProtocolDriver Create(ProtocolIdentifier protocol, DeviceConnection connection)
    {
        _last = new BlockingDisposeDriver();
        return _last;
    }
}

