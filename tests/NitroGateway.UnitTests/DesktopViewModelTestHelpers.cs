using NitroGateway.DeviceManagement;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027：按调用顺序出队的设备目录缓存 fake。
/// 用 TaskCompletionSource 注入可控制完成时机的查询，模拟慢查询与乱序完成（竞态测试）。
/// </summary>
internal sealed class StagedSnapshotCache : IDeviceSnapshotCache
{
    private readonly Queue<Task<OperationResult<IReadOnlyList<Device>>>> _results = new();

    /// <summary>排入一个查询结果（可为未完成的 TCS.Task）。</summary>
    public void Enqueue(Task<OperationResult<IReadOnlyList<Device>>> result) => _results.Enqueue(result);

    /// <summary>排入一个立即成功的查询结果。</summary>
    public void EnqueueSuccess(params Device[] devices) =>
        Enqueue(Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success((IReadOnlyList<Device>)devices)));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default) =>
        _results.Count > 0
            ? _results.Dequeue()
            : Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(Array.Empty<Device>()));

    public void Invalidate() { }
}

/// <summary>
/// ADR-027：按调用顺序出队的时序存储 fake，记录分页调用参数（limit/offset）供断言。
/// ADR-047：store 查询在 VM 内经 Task.Run 移到线程池执行，出队可能跨线程，
/// 故对队列/PagedCalls 加锁，并暴露 <see cref="PagedDequeueCount"/> 供测试等待某次查询真正触达存储。
/// </summary>
internal sealed class StagedMeasurementStore : IMeasurementStore
{
    private readonly object _gate = new();
    private readonly Queue<Task<OperationResult<IReadOnlyList<PointSnapshot>>>> _paged = new();
    private readonly Queue<Task<OperationResult<IReadOnlyList<PointSnapshot>>>> _latest = new();
    private int _pagedDequeueCount;
    private int _latestDequeueCount;

    /// <summary>分页查询调用记录：(limit, offset)。</summary>
    public List<(int Limit, int Offset)> PagedCalls { get; } = new();

    /// <summary>分页查询已出队次数（Task.Run 下出队在线程池，供测试等待查询真正发起后再触发下一次）。</summary>
    public int PagedDequeueCount => Volatile.Read(ref _pagedDequeueCount);

    /// <summary>
    /// 最新值查询（QueryLatestAsync）已出队次数（ADR-050）。Task.Run 下出队在线程池，
    /// 供测试断言「帧驱动切设备全程未触发 DB 最新值查询」或等待兜底查询真正发起。
    /// </summary>
    public int LatestDequeueCount => Volatile.Read(ref _latestDequeueCount);

    public void EnqueuePaged(Task<OperationResult<IReadOnlyList<PointSnapshot>>> result)
    {
        lock (_gate) _paged.Enqueue(result);
    }

    public void EnqueueLatest(Task<OperationResult<IReadOnlyList<PointSnapshot>>> result)
    {
        lock (_gate) _latest.Enqueue(result);
    }

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _pagedDequeueCount);
            PagedCalls.Add((limit, offset));
            return _paged.Count > 0
                ? _paged.Dequeue()
                : Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(Array.Empty<PointSnapshot>()));
        }
    }

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _latestDequeueCount);
            return _latest.Count > 0
                ? _latest.Dequeue()
                : Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success(Array.Empty<PointSnapshot>()));
        }
    }

    public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>EventBridge 测试用空转发缓冲（与 DesktopShellRegistrationTests 的私有桩等价）。</summary>
internal sealed class StubForwardBuffer : IForwardBuffer
{
    public int Count => 0;
    public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>测试辅助：轮询等待条件成立，带超时（竞态测试等异步延续落地）。</summary>
internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待条件超时");
            await Task.Delay(10);
        }
    }
}

/// <summary>测试辅助：构造设备（含点位）。</summary>
internal static class TestDevices
{
    public static Device Device(string name, params DevicePoint[] points)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            Name = name,
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
            Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
        };
        foreach (var point in points)
            device.AddPoint(point);
        return device;
    }

    public static DevicePoint Point(string name, object? value = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Address = "40001",
        DataType = DataType.Float,
        Enabled = true
    };

    public static PointSnapshot Snapshot(Guid deviceId, Guid pointId, double value, DateTime? timestamp = null) => new()
    {
        DeviceId = deviceId,
        DevicePointId = pointId,
        Value = value,
        Timestamp = timestamp ?? DateTime.UtcNow
    };
}

/// <summary>IUiTimer 测试替身：手动触发 Tick，记录 Start/Stop 调用（轮询节奏注入）。</summary>
internal sealed class FakeUiTimer : IUiTimer
{
    /// <inheritdoc />
    public event EventHandler? Tick;

    /// <summary>Start 调用次数。</summary>
    public int StartCalls { get; private set; }

    /// <summary>Stop 调用次数。</summary>
    public int StopCalls { get; private set; }

    /// <summary>当前是否处于启动态（Start 后未 Stop）。</summary>
    public bool IsStarted { get; private set; }

    /// <inheritdoc />
    public void Start()
    {
        StartCalls++;
        IsStarted = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        StopCalls++;
        IsStarted = false;
    }

    /// <summary>手动触发一个周期（等价 DispatcherTimer 到达间隔）。</summary>
    public void RaiseTick() => Tick?.Invoke(this, EventArgs.Empty);
}
