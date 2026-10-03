using NitroGateway.Collection;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Storage.TimeSeries;

namespace NitroGateway.LoadTests;

/// <summary>固定返回预置设备列表的设备目录替身。</summary>
public sealed class FakeDeviceManager : IDeviceManager
{
    private readonly IReadOnlyList<Device> _devices;

    public FakeDeviceManager(IReadOnlyList<Device> devices) => _devices = devices;

    public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Success(device));

    public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Failure(OperationalError.NotFound("设备不存在")));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(_devices));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
        => GetAllAsync(ct);

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(_devices));

    public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
        => GetAllIncludingDeletedAsync(ct);

    public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(_devices));

    public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult<Device>.Success(_devices.FirstOrDefault(d => d.Id == deviceId)!));

    public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}

/// <summary>无副作用健康监控替身（始终无快照 → DeviceCollector 回退到设备状态）。</summary>
public sealed class FakeDeviceHealthMonitor : IDeviceHealthMonitor
{
    public void ReportSuccess(Guid deviceId, string? deviceName) { }
    public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
    public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
    public int FailureThreshold => 3;
    public int RecoveryThreshold => 3;
    public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => null;
    public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => [];
    public void Remove(Guid deviceId) { }
    public void AddListener(IDeviceHealthListener listener) { }
}

/// <summary>
/// 假设备读取器：绕开协议驱动池，直接返回预置的原始值列表，把负载精确压到
/// Pipeline/Dispatcher/Store。缓存每台设备的原始值，避免分配主导测量结果。
/// </summary>
public sealed class FakeDeviceReader : IDeviceReader
{
    private readonly Dictionary<Guid, IReadOnlyList<RawPointValue>> _cache = new();
    private readonly Dictionary<Guid, IReadOnlyList<DevicePoint>> _points = new();
    private long _rawValuesReturned;
    private long _readCalls;

    public FakeDeviceReader(IEnumerable<Device> devices)
    {
        foreach (var device in devices)
        {
            var points = device.Points.Where(p => p.Enabled).ToList();
            _points[device.Id] = points;
            _cache[device.Id] = BuildValues(points);
        }
    }

    /// <summary>累计产出（协议解码后）的原始值数量，作为「应落库点数」基准。</summary>
    public long RawValuesReturned => Interlocked.Read(ref _rawValuesReturned);

    public long ReadCalls => Interlocked.Read(ref _readCalls);

    public IReadOnlyList<DevicePoint>? GetDuePoints(Device device)
        => _points.TryGetValue(device.Id, out var pts) ? pts : device.Points.Where(p => p.Enabled).ToList();

    public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadDeviceAsync(
        Device device, CancellationToken ct)
    {
        Interlocked.Increment(ref _readCalls);
        var values = _cache[device.Id];
        Interlocked.Add(ref _rawValuesReturned, values.Count);
        return Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(values));
    }

    private static IReadOnlyList<RawPointValue> BuildValues(IReadOnlyList<DevicePoint> points)
    {
        var ts = DateTime.UtcNow;
        var list = new List<RawPointValue>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            object value = point.DataType switch
            {
                DataType.Bool => i % 2 == 0,
                DataType.Double => (i % 1000) + 0.125,
                _ => (double)(i % 1000) + 0.5
            };
            list.Add(new RawPointValue { Point = point, Value = value, Timestamp = ts });
        }

        return list;
    }
}

/// <summary>假时序存储：只累计写入点数，隔离 SQLite I/O，用于测 CPU/Channel。</summary>
public sealed class FakeMeasurementStore : IMeasurementStore
{
    private long _points;
    private long _batches;

    public long PointCount => Interlocked.Read(ref _points);

    public long BatchCount => Interlocked.Read(ref _batches);

    public Task<OperationResult> WriteAsync(IReadOnlyList<PointSnapshot> snapshots, CancellationToken ct = default)
    {
        Interlocked.Add(ref _points, snapshots.Count);
        Interlocked.Increment(ref _batches);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryAsync(
        Guid deviceId, Guid pointId, DateTime from, DateTime to, CancellationToken ct = default)
        => Empty();

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryByDeviceAsync(
        Guid deviceId, DateTime from, DateTime to, CancellationToken ct = default)
        => Empty();

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryPagedAsync(
        Guid deviceId, Guid? pointId, DateTime from, DateTime to, int limit, int offset, CancellationToken ct = default)
        => Empty();

    public Task<OperationResult<IReadOnlyList<PointSnapshot>>> QueryLatestAsync(
        Guid deviceId, Guid? pointId, CancellationToken ct = default)
        => Empty();

    public Task<OperationResult> PurgeAsync(DateTime before, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    private static Task<OperationResult<IReadOnlyList<PointSnapshot>>> Empty()
        => Task.FromResult(OperationResult<IReadOnlyList<PointSnapshot>>.Success([]));
}

/// <summary>空转发缓冲：L2 只关心采集→落库链路，丢弃转发侧写入。</summary>
public sealed class NullForwardBuffer : IForwardBuffer
{
    public int Count => 0;

    public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, string channel, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<BatchMeasurements>>.Success([]));

    public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<DeadLetterEntry>>.Success([]));

    public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());

    public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default)
        => Task.FromResult(OperationResult.Success());
}
