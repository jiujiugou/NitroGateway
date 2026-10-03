using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;
using Xunit;

namespace NitroGateway.UnitTests.Devices;

/// <summary>
/// 设备管理器单元测试——FakeDeviceRepository 模拟数据层。
///
/// <para>DeviceManager 的核心业务逻辑不是 CRUD（那由 Repository 负责），而是：
/// 1. 状态统一入口：UpdateStatusAsync 持久化状态变更并驱逐驱动池连接
/// 2. SetMaintenanceAsync 语义：将设备标记为维护模式或 Unknown
/// 3. 空 ID 拒绝</para>
///
/// <para>FakeDeviceRepository 是一个内存字典，完美模拟 EF Core 的同步行为，
/// 不需要真实 SQLite 连接。</para>
/// </summary>
public class DeviceManagerTests
{
    private readonly FakeDeviceRepository _repo = new();
    private readonly DeviceManager _manager;
    private readonly FakeDeviceHealthMonitor _healthMonitor = new();
    private readonly FakeDriverPool _driverPool = new();
    private readonly FakeDeviceSnapshotCache _cache = new();
    public DeviceManagerTests()
    {
        _manager = new DeviceManager(_repo, _healthMonitor, _driverPool, _cache, NullLogger<DeviceManager>.Instance);
    }

    /// <summary>正常注册设备，ID + Name 正确返回。</summary>
    /// <summary>设备注册（新建/更新）后应驱逐池中旧驱动，保证下一轮用新连接参数重建</summary>
    [Fact]
    public async Task RegisterAsync_EvictsDriverFromPool()
    {
        var device = MakeDevice("PLC01");
        var result = await _manager.RegisterAsync(device);
        Assert.True(result.IsSuccess);
        Assert.Contains(device.Id, _driverPool.Evicted);
    }

    [Fact]
    public async Task RegisterAsync_CreatesDevice()
    {
        var device = MakeDevice("PLC01");
        var result = await _manager.RegisterAsync(device);
        Assert.True(result.IsSuccess);
        Assert.Equal("PLC01", result.Value!.Name);
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>空 Guid 的设备注册应被拒绝，错误信息包含"不能为空"。</summary>
    [Fact]
    public async Task RegisterAsync_EmptyId_Rejected()
    {
        var device = MakeDevice("Bad"); device = new Device { Id = Guid.Empty, Name = "Bad",
            Protocol = device.Protocol, Connection = device.Connection };
        var result = await _manager.RegisterAsync(device);
        Assert.False(result.IsSuccess);
        Assert.Contains("不能为空", result.Error!.Message);
    }

    /// <summary>Offline → Online 状态变更（HealthMonitor 通过 PersistenceListener 调用）</summary>
    [Fact]
    public async Task UpdateStatus_OfflineToOnline_Succeeds()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC", status: DeviceStatus.Offline);
        var result = await _manager.UpdateStatusAsync(id, DeviceStatus.Online);
        Assert.True(result.IsSuccess);
        Assert.Equal(DeviceStatus.Online, _repo.Devices[id].Status);
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>正常状态转换（Online → Maintenance）应成功。</summary>
    [Fact]
    public async Task UpdateStatus_OnlineToMaintenance_Succeeds()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC");
        var result = await _manager.UpdateStatusAsync(id, DeviceStatus.Maintenance);
        Assert.True(result.IsSuccess);
        Assert.Equal(DeviceStatus.Maintenance, _repo.Devices[id].Status);
    }

    /// <summary>SetMaintenanceAsync(true) → Maintenance，SetMaintenanceAsync(false) → Unknown。</summary>
    [Fact]
    public async Task SetMaintenance_True_SetsToMaintenance()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC");
        await _manager.SetMaintenanceAsync(id, true);
        Assert.Equal(DeviceStatus.Maintenance, _repo.Devices[id].Status);
    }

    /// <summary>GetAsync 返回存在的设备。</summary>
    [Fact]
    public async Task GetAsync_ExistingDevice_ReturnsDevice()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC");
        var result = await _manager.GetAsync(id);
        Assert.True(result.IsSuccess);
        Assert.Equal("PLC", result.Value!.Name);
    }

    /// <summary>UnregisterAsync 后设备从字典中移除。</summary>
    [Fact]
    public async Task UnregisterAsync_RemovesDevice()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC");
        await _manager.UnregisterAsync(id);
        Assert.False(_repo.Devices.ContainsKey(id));  // 已删除
    }

    /// <summary>注销设备后健康快照应被清理，不能残留内存。</summary>
    [Fact]
    public async Task UnregisterAsync_ClearsHealthSnapshot()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC");
        _healthMonitor.UpdateStatus(id, DeviceStatus.Online);

        await _manager.UnregisterAsync(id);

        Assert.Contains(id, _healthMonitor.Removed);
    }

    /// <summary>获取不存在的设备应返回 Failure。</summary>
    [Fact]
    public async Task GetAsync_NonExistentDevice_ReturnsFailure()
    {
        var result = await _manager.GetAsync(Guid.NewGuid());
        Assert.False(result.IsSuccess);
    }

    // ── 注册/注销/状态变更的副作用 ──

    /// <summary>注册后应把设备配置状态同步到健康监控。</summary>
    [Fact]
    public async Task RegisterAsync_UpdatesHealthMonitorStatus()
    {
        var device = MakeDevice("PLC", status: DeviceStatus.Maintenance);
        await _manager.RegisterAsync(device);
        Assert.Equal(DeviceStatus.Maintenance, _healthMonitor.Statuses[device.Id]);
    }

    /// <summary>注销后应驱逐驱动池连接，避免悬挂长连接。</summary>
    [Fact]
    public async Task UnregisterAsync_EvictsDriverFromPool()
    {
        var device = MakeDevice("PLC");
        _repo.Devices[device.Id] = device;
        await _manager.UnregisterAsync(device.Id);
        Assert.Contains(device.Id, _driverPool.Evicted);
    }

    /// <summary>注销后应失效快照缓存。</summary>
    [Fact]
    public async Task UnregisterAsync_InvalidatesCache()
    {
        var device = MakeDevice("PLC");
        _repo.Devices[device.Id] = device;
        await _manager.UnregisterAsync(device.Id);
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>状态变更后应驱逐驱动池连接（下一轮按需重建）。</summary>
    [Fact]
    public async Task UpdateStatus_EvictsDriverFromPool()
    {
        var device = MakeDevice("PLC", status: DeviceStatus.Offline);
        _repo.Devices[device.Id] = device;
        await _manager.UpdateStatusAsync(device.Id, DeviceStatus.Online);
        Assert.Contains(device.Id, _driverPool.Evicted);
    }

    /// <summary>状态未变化时走早退：不落库、不驱逐、不记日志。</summary>
    [Fact]
    public async Task UpdateStatus_SameStatus_NoSaveNoEvict()
    {
        var device = MakeDevice("PLC", status: DeviceStatus.Online);
        _repo.Devices[device.Id] = device;

        var result = await _manager.UpdateStatusAsync(device.Id, DeviceStatus.Online);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _repo.SaveCalls);
        Assert.Empty(_driverPool.Evicted);
    }

    /// <summary>更新不存在设备的状态应返回 Failure。</summary>
    [Fact]
    public async Task UpdateStatus_NonExistent_Fails()
    {
        var result = await _manager.UpdateStatusAsync(Guid.NewGuid(), DeviceStatus.Online);
        Assert.True(result.IsFailure);
    }

    /// <summary>SetMaintenanceAsync(false) → Unknown（对称于 true→Maintenance）。</summary>
    [Fact]
    public async Task SetMaintenance_False_SetsToUnknown()
    {
        var device = MakeDevice("PLC");
        _repo.Devices[device.Id] = device;
        await _manager.SetMaintenanceAsync(device.Id, false);
        Assert.Equal(DeviceStatus.Unknown, device.Status);
    }

    // ── ADR-033/035：tombstone 过滤 / 站点隔离 / 软删 ──

    /// <summary>GetAllAsync 应过滤 tombstone，只返回存活设备。</summary>
    [Fact]
    public async Task GetAllAsync_FiltersOutDeleted()
    {
        var alive = MakeDevice("Alive");
        var deleted = MakeDevice("Deleted");
        deleted.IsDeleted = true;
        _cache.Devices = [alive, deleted];

        var result = await _manager.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(alive.Id, result.Value![0].Id);
    }

    /// <summary>GetAllAsync 非空 siteId 只返回该站点设备（ADR-035）。</summary>
    [Fact]
    public async Task GetAllAsync_SiteId_FiltersBySite()
    {
        var a = MakeDevice("A"); a.SiteId = "site-1";
        var b = MakeDevice("B"); b.SiteId = "site-2";
        _cache.Devices = [a, b];

        var result = await _manager.GetAllAsync("site-1");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(a.Id, result.Value![0].Id);
    }

    /// <summary>siteId 为空时不过滤，返回全部存活设备（兼容旧调用）。</summary>
    [Fact]
    public async Task GetAllAsync_EmptySiteId_ReturnsAllAlive()
    {
        var a = MakeDevice("A"); a.SiteId = "site-1";
        var b = MakeDevice("B"); b.SiteId = "site-2";
        _cache.Devices = [a, b];

        var result = await _manager.GetAllAsync((string?)null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    /// <summary>GetAllIncludingDeletedAsync 应保留 tombstone（同步导出需要完整视图）。</summary>
    [Fact]
    public async Task GetAllIncludingDeletedAsync_IncludesTombstones()
    {
        var alive = MakeDevice("Alive");
        var deleted = MakeDevice("Deleted");
        deleted.IsDeleted = true;
        _cache.Devices = [alive, deleted];

        var result = await _manager.GetAllIncludingDeletedAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    /// <summary>GetAllIncludingDeletedAsync 非空 siteId 仍按站点过滤。</summary>
    [Fact]
    public async Task GetAllIncludingDeletedAsync_SiteId_FiltersBySite()
    {
        var a = MakeDevice("A"); a.SiteId = "site-1";
        var b = MakeDevice("B"); b.SiteId = "site-2";
        _cache.Devices = [a, b];

        var result = await _manager.GetAllIncludingDeletedAsync("site-1");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!);
        Assert.Equal(a.Id, result.Value![0].Id);
    }

    /// <summary>GetAllIncludingDeletedAsync siteId 为空时不过滤，返回全部（含 tombstone）。</summary>
    [Fact]
    public async Task GetAllIncludingDeletedAsync_EmptySiteId_ReturnsAll()
    {
        var a = MakeDevice("A"); a.SiteId = "site-1";
        var b = MakeDevice("B"); b.SiteId = "site-2";
        b.IsDeleted = true;
        _cache.Devices = [a, b];

        var result = await _manager.GetAllIncludingDeletedAsync((string?)null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Count);
    }

    /// <summary>GetIncludingDeletedAsync 按 ID 返回 tombstone（同步接收端判断拒绝复活）。</summary>
    [Fact]
    public async Task GetIncludingDeletedAsync_ReturnsTombstone()
    {
        var device = MakeDevice("Deleted");
        device.IsDeleted = true;
        _repo.Devices[device.Id] = device;

        var result = await _manager.GetIncludingDeletedAsync(device.Id);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsDeleted);
    }

    /// <summary>软删存活设备：置 tombstone、盖章 UpdatedAt、驱逐驱动、清快照、失效缓存。</summary>
    [Fact]
    public async Task SoftDeleteAsync_Existing_SetsTombstone()
    {
        var device = MakeDevice("PLC");
        _repo.Devices[device.Id] = device;
        var before = device.UpdatedAt;

        var result = await _manager.SoftDeleteAsync(device.Id);

        Assert.True(result.IsSuccess);
        Assert.True(device.IsDeleted);
        Assert.True(device.UpdatedAt > before);
        Assert.Contains(device.Id, _driverPool.Evicted);
        Assert.Contains(device.Id, _healthMonitor.Removed);
        Assert.Equal(1, _cache.InvalidateCount);
    }

    /// <summary>软删不存在的设备应幂等成功（同步路径按 ID 处理）。</summary>
    [Fact]
    public async Task SoftDeleteAsync_NonExistent_IdempotentSuccess()
    {
        var result = await _manager.SoftDeleteAsync(Guid.NewGuid());
        Assert.True(result.IsSuccess);
        Assert.Empty(_driverPool.Evicted);
    }

    /// <summary>软删已删除设备应幂等成功且不重复落库。</summary>
    [Fact]
    public async Task SoftDeleteAsync_AlreadyDeleted_IdempotentWithoutResave()
    {
        var device = MakeDevice("PLC");
        device.IsDeleted = true;
        _repo.Devices[device.Id] = device;

        var result = await _manager.SoftDeleteAsync(device.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _repo.SaveCalls);
    }

    // ── Helpers ──

    private static Device MakeDevice(string name, DeviceStatus status = DeviceStatus.Online) => new()
    {
        Id = Guid.NewGuid(), Name = name,
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" },
        Status = status
    };

    /// <summary>FakeDeviceRepository：内存字典模拟 SQLite 持久化层。</summary>
    private sealed class FakeDeviceRepository : IDeviceRepository
    {
        public readonly Dictionary<Guid, Device> Devices = [];

        public int SaveCalls { get; private set; }

        public Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default)
        {
            SaveCalls++;
            Devices[device.Id] = device;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default)
        {
            Devices.Remove(deviceId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default)
        {
            if (Devices.TryGetValue(deviceId, out var d))
                return Task.FromResult(OperationResult<Device>.Success(d));
            return Task.FromResult(OperationResult<Device>.Failure(
                OperationalError.NotFound("设备不存在")));
        }

        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(Devices.Values.ToList()));

        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(
            DeviceStatus status, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(
                Devices.Values.Where(d => d.Status == status).ToList()));
    }
    private sealed class FakeDriverPool : IProtocolDriverPool
    {
        public List<Guid> Evicted { get; } = new();

        public IProtocolDriver GetOrCreate(Device device) => throw new NotImplementedException();

        public void Evict(Guid deviceId) => Evicted.Add(deviceId);

        public void Dispose() { }
    }
    private sealed class FakeDeviceSnapshotCache : IDeviceSnapshotCache
    {
        public IReadOnlyList<Device> Devices { get; set; } = [];
        public int InvalidateCount { get; private set; }
        public void Invalidate() => InvalidateCount++;
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<Device>>.Success(Devices));
    }
    private sealed class FakeDeviceHealthMonitor: IDeviceHealthMonitor
    {
        public Dictionary<Guid, DeviceStatus> Statuses = new();

        public List<Guid> Removed { get; } = new();

        public int FailureThreshold => throw new NotImplementedException();

        public int RecoveryThreshold => throw new NotImplementedException();

        public void AddListener(IDeviceHealthListener listener)
        {
            throw new NotImplementedException();
        }

        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots()
        {
            throw new NotImplementedException();
        }

        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId)
        {
            throw new NotImplementedException();
        }

        public void ReportFailure(Guid deviceId, string? deviceName, string reason)
        {
            throw new NotImplementedException();
        }

        public void ReportSuccess(Guid deviceId, string? deviceName)
        {
            throw new NotImplementedException();
        }

        public void UpdateStatus(Guid deviceId, DeviceStatus status)
        {
            if(Statuses.ContainsKey(deviceId))
                Statuses[deviceId] = status;
            else
                Statuses.Add(deviceId, status);
        }

        public void Remove(Guid deviceId)
        {
            Removed.Add(deviceId);
            Statuses.Remove(deviceId);
        }
    }
}
