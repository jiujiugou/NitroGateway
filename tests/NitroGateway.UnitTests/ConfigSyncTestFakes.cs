using NitroGateway.Desktop.Services;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.UnitTests;

/// <summary>ADR-033 测试替身：配置同步设置（内存版，无文件 IO）。</summary>
internal sealed class StubSyncSettingsStore : ICenterSyncSettingsStore
{
    public CenterSyncSettings Settings { get; set; } = new();

    public CenterSyncSettings Load() => Settings;
    public void Save(CenterSyncSettings settings) => Settings = settings;
}

/// <summary>ADR-033 测试替身：中心客户端（可编程快照/上报结果 + 记录调用）。</summary>
internal sealed class StubSyncCenterClient : ICenterConfigClient
{
    public OperationResult<CenterSyncSnapshot>? SnapshotResult { get; set; }
    public OperationResult<IReadOnlyList<CenterSyncChangeResult>> PushResult { get; set; } =
        OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success([]);

    public int FetchSnapshotCalls { get; private set; }
    public int FetchSyncSnapshotCalls { get; private set; }
    public int PushCalls { get; private set; }
    public IReadOnlyList<CenterSyncChange>? LastChanges { get; private set; }
    public string LastSiteId { get; private set; } = "";

    public Task<OperationResult<IReadOnlyList<Device>>> FetchSnapshotAsync(
        string centerUrl, string token, CancellationToken ct = default)
    {
        FetchSnapshotCalls++;
        return Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(Array.Empty<Device>()));
    }

    public Task<OperationResult<CenterSyncSnapshot>> FetchSyncSnapshotAsync(
        string centerUrl, string token, CancellationToken ct = default)
    {
        FetchSyncSnapshotCalls++;
        return Task.FromResult(SnapshotResult ?? OperationResult<CenterSyncSnapshot>.Failure(
            OperationalError.General("未配置快照结果")));
    }

    public Task<OperationResult<IReadOnlyList<CenterSyncChangeResult>>> PushChangesAsync(
        string centerUrl, string token, string siteId, IReadOnlyList<CenterSyncChange> changes,
        CancellationToken ct = default)
    {
        PushCalls++;
        LastChanges = changes;
        LastSiteId = siteId;
        return Task.FromResult(PushResult);
    }
}

/// <summary>
/// ADR-033 阶段 4 测试替身：内存 outbox（与 SQLite 实现同语义：
/// 设备/点位各用固定键，删除后重建原地替换行类型）。
/// </summary>
internal sealed class StubConfigSyncOutboxStore : IConfigSyncOutboxStore
{
    public List<ConfigSyncOutboxRow> Rows { get; } = [];

    /// <summary>全部写入调用记录（含重复键覆盖前的记录）。</summary>
    public List<(ConfigSyncOutboxKind Kind, Guid DeviceId, Guid? PointId)> Records { get; } = [];

    public List<Guid> ClearedDevices { get; } = [];
    public int ClearAllCalls { get; private set; }
    public bool FailNextClear { get; set; }

    public Task<OperationResult> RecordDeviceAsync(Device device, CancellationToken ct = default)
        => Record(ConfigSyncOutboxKind.Device, device.Id, null);

    public Task<OperationResult> RecordDeviceDeleteAsync(Guid deviceId, CancellationToken ct = default)
        => Record(ConfigSyncOutboxKind.DeviceDelete, deviceId, null);

    public Task<OperationResult> RecordPointAsync(Guid deviceId, DevicePoint point, CancellationToken ct = default)
        => Record(ConfigSyncOutboxKind.Point, deviceId, point.Id);

    public Task<OperationResult> RecordPointDeleteAsync(Guid deviceId, Guid pointId, CancellationToken ct = default)
        => Record(ConfigSyncOutboxKind.PointDelete, deviceId, pointId);

    public Task<OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>> GetPendingAsync(CancellationToken ct = default)
        => Task.FromResult(OperationResult<IReadOnlyList<ConfigSyncOutboxRow>>.Success(Rows.ToList()));

    public Task<OperationResult> ClearAsync(
        ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId = null, CancellationToken ct = default)
    {
        var key = Key(kind, deviceId, pointId);
        Rows.RemoveAll(r => Key(r.Kind, r.DeviceId, r.PointId) == key);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> ClearForDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        ClearedDevices.Add(deviceId);
        if (FailNextClear)
        {
            FailNextClear = false;
            return Task.FromResult(OperationResult.Failure(OperationalError.General("clear boom")));
        }
        Rows.RemoveAll(r => r.DeviceId == deviceId);
        return Task.FromResult(OperationResult.Success());
    }

    public Task<OperationResult> ClearAllAsync(CancellationToken ct = default)
    {
        ClearAllCalls++;
        Rows.Clear();
        return Task.FromResult(OperationResult.Success());
    }

    private Task<OperationResult> Record(ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId)
    {
        Records.Add((kind, deviceId, pointId));
        var key = Key(kind, deviceId, pointId);
        Rows.RemoveAll(r => Key(r.Kind, r.DeviceId, r.PointId) == key);
        Rows.Add(new ConfigSyncOutboxRow { Kind = kind, DeviceId = deviceId, PointId = pointId });
        return Task.FromResult(OperationResult.Success());
    }

    private static (bool IsPoint, Guid DeviceId, Guid? PointId) Key(
        ConfigSyncOutboxKind kind, Guid deviceId, Guid? pointId)
        => kind is ConfigSyncOutboxKind.Point or ConfigSyncOutboxKind.PointDelete
            ? (true, deviceId, pointId)
            : (false, deviceId, null);
}
