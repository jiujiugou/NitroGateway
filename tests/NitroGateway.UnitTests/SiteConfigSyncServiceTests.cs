using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-033 阶段 3/4：配置自动同步——双向 UpdatedAt 合并（中心较新覆盖/本地较新保留）、
/// 中心 tombstone 驱动本地删除、中心缺失的现场临时设备保留、断网静默跳过、outbox 上报与清行。
/// </summary>
public sealed class SiteConfigSyncServiceTests
{
    private readonly StubSyncSettingsStore _settings = new()
    {
        Settings = new CenterSyncSettings { CenterUrl = "http://center:5100", CenterToken = "tok" }
    };
    private readonly StubSyncCenterClient _client = new();
    private readonly StubConfigSyncOutboxStore _outbox = new();
    private readonly StubDeviceManager _manager = new();
    private readonly StubPointManager _points = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ServiceProvider _provider;

    public SiteConfigSyncServiceTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => _manager);
        services.AddScoped<IPointManager>(_ => _points);
        _provider = services.BuildServiceProvider();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task Empty_center_settings_skips_pull_and_push()
    {
        _settings.Settings = new CenterSyncSettings();
        var service = CreateService();

        await service.SyncOnceAsync();

        Assert.Equal(0, _client.FetchSyncSnapshotCalls);
        Assert.Equal(0, _client.PushCalls);
    }

    [Fact]
    public async Task Center_newer_overwrites_local_device_and_points_and_clears_outbox()
    {
        var deviceId = Guid.NewGuid();
        var localPoint = TestDevices.Point("本地点位");
        var local = Device(deviceId, "本地名称");
        local.AddPoint(localPoint);
        local.UpdatedAt = DateTime.UtcNow.AddHours(-2);
        localPoint.UpdatedAt = local.UpdatedAt;
        _manager.LocalDevices = [local];
        _points.Seed(deviceId, localPoint);
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.Device, DeviceId = deviceId });

        var centerPoint = TestDevices.Point("中心点位");
        var center = Device(deviceId, "中心名称");
        centerPoint.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        center.AddPoint(centerPoint);
        center.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(new[] { center }, DateTime.UtcNow));

        var service = CreateService();
        await service.SyncOnceAsync();

        // 中心较新 → 覆盖本地（RegisterAsync 收到中心版本）
        var registered = Assert.Single(_manager.Registered);
        Assert.Equal("中心名称", registered.Name);
        Assert.Equal(center.UpdatedAt, registered.UpdatedAt);
        // 本地多余点位被移除，中心点位导入
        Assert.Contains((deviceId, localPoint.Id), _points.Removed);
        Assert.Single(_points.Imported);
        // 本地待上报改动被裁决丢弃 → 清 outbox，不再上报
        Assert.Contains(deviceId, _outbox.ClearedDevices);
        Assert.Empty(_outbox.Rows);
        Assert.Equal(0, _client.PushCalls);
    }

    [Fact]
    public async Task Local_newer_keeps_local_and_pushes_pending()
    {
        var deviceId = Guid.NewGuid();
        var local = Device(deviceId, "本地新名称");
        local.UpdatedAt = DateTime.UtcNow;
        _manager.LocalDevices = [local];
        _manager.GetResult = local;
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.Device, DeviceId = deviceId });

        var center = Device(deviceId, "中心旧名称");
        center.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(new[] { center }, DateTime.UtcNow));
        _client.PushResult = OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success(
            [new CenterSyncChangeResult(deviceId.ToString(), "accepted")]);

        var service = CreateService();
        await service.SyncOnceAsync();

        // 本地较新 → 不覆盖、不删除
        Assert.Empty(_manager.Registered);
        Assert.Empty(_manager.Unregistered);
        // 待上报改动上报中心（带本地全量状态），accepted 后清 outbox
        Assert.Equal(1, _client.PushCalls);
        var change = Assert.Single(_client.LastChanges!);
        Assert.Equal(deviceId, change.DeviceId);
        Assert.False(change.Deleted);
        Assert.Equal("本地新名称", change.Device!.Name);
        Assert.Equal("site-a", _client.LastSiteId);
        Assert.Contains(deviceId, _outbox.ClearedDevices);
        Assert.Empty(_outbox.Rows);
    }

    [Fact]
    public async Task Center_tombstone_deletes_local_and_clears_outbox()
    {
        var deviceId = Guid.NewGuid();
        var local = Device(deviceId, "将被删");
        _manager.LocalDevices = [local];
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.Device, DeviceId = deviceId });

        var center = Device(deviceId, "将被删");
        center.IsDeleted = true;
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(new[] { center }, DateTime.UtcNow));

        var service = CreateService();
        await service.SyncOnceAsync();

        Assert.Equal(deviceId, Assert.Single(_manager.Unregistered));
        Assert.Contains(deviceId, _outbox.ClearedDevices);
        Assert.Empty(_outbox.Rows);
        Assert.Equal(0, _client.PushCalls);
    }

    [Fact]
    public async Task Center_missing_local_device_is_kept_and_pushed()
    {
        var deviceId = Guid.NewGuid();
        var local = Device(deviceId, "现场临时设备");
        local.UpdatedAt = DateTime.UtcNow;
        _manager.LocalDevices = [local];
        _manager.GetResult = local;
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.Device, DeviceId = deviceId });
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(Array.Empty<Device>(), DateTime.UtcNow));
        _client.PushResult = OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success(
            [new CenterSyncChangeResult(deviceId.ToString(), "accepted")]);

        var service = CreateService();
        await service.SyncOnceAsync();

        // 中心快照缺失 → 现场临时设备保留（不删除、不覆盖）
        Assert.Empty(_manager.Unregistered);
        Assert.Empty(_manager.Registered);
        Assert.Equal(1, _client.PushCalls);
        Assert.Equal("现场临时设备", Assert.Single(_client.LastChanges!).Device!.Name);
        Assert.Empty(_outbox.Rows);
    }

    [Fact]
    public async Task Fetch_failure_skips_silently_without_touching_local()
    {
        _manager.LocalDevices = [TestDevices.Device("PLC-1")];
        _outbox.Rows.Add(new ConfigSyncOutboxRow
        {
            Kind = ConfigSyncOutboxKind.Device,
            DeviceId = Guid.NewGuid()
        });
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Failure(
            OperationalError.General("无法连接中心"));

        var service = CreateService();
        await service.SyncOnceAsync();

        Assert.Empty(_manager.Registered);
        Assert.Empty(_manager.Unregistered);
        Assert.Equal(0, _client.PushCalls);
        Assert.Single(_outbox.Rows); // outbox 保留，下次重试
    }

    [Fact]
    public async Task Device_delete_row_pushes_tombstone()
    {
        var deviceId = Guid.NewGuid();
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.DeviceDelete, DeviceId = deviceId });
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(Array.Empty<Device>(), DateTime.UtcNow));
        _client.PushResult = OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success(
            [new CenterSyncChangeResult(deviceId.ToString(), "accepted")]);

        var service = CreateService();
        await service.SyncOnceAsync();

        var change = Assert.Single(_client.LastChanges!);
        Assert.True(change.Deleted);
        Assert.Equal(deviceId, change.DeviceId);
        Assert.Null(change.Device);
        Assert.Contains(deviceId, _outbox.ClearedDevices);
    }

    [Fact]
    public async Task Point_delete_rows_are_collected_into_deleted_point_ids()
    {
        var deviceId = Guid.NewGuid();
        var deletedPointId = Guid.NewGuid();
        var livePoint = TestDevices.Point("存活点位");
        var device = Device(deviceId, "PLC-1");
        device.AddPoint(livePoint);
        _manager.GetResult = device;
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.Point, DeviceId = deviceId, PointId = livePoint.Id });
        _outbox.Rows.Add(new ConfigSyncOutboxRow { Kind = ConfigSyncOutboxKind.PointDelete, DeviceId = deviceId, PointId = deletedPointId });
        _client.SnapshotResult = OperationResult<CenterSyncSnapshot>.Success(
            new CenterSyncSnapshot(Array.Empty<Device>(), DateTime.UtcNow));
        _client.PushResult = OperationResult<IReadOnlyList<CenterSyncChangeResult>>.Success(
            [new CenterSyncChangeResult(deviceId.ToString(), "accepted")]);

        var service = CreateService();
        await service.SyncOnceAsync();

        var change = Assert.Single(_client.LastChanges!);
        Assert.False(change.Deleted);
        Assert.Equal(deletedPointId, Assert.Single(change.DeletedPointIds));
        Assert.Contains(livePoint.Id, change.Device!.Points.Select(p => p.Id));
        Assert.Empty(_outbox.Rows);
    }

    private SiteConfigSyncService CreateService() => new(
        _settings, _client, _outbox, _scopeFactory,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Site:Id"] = "site-a",
            ["ConfigSync:PollIntervalSeconds"] = "5"
        }).Build(),
        NullLogger<SiteConfigSyncService>.Instance);

    private static Device Device(Guid id, string name)
    {
        var device = TestDevices.Device(name);
        return new Device
        {
            Id = id,
            Name = device.Name,
            Description = device.Description,
            Protocol = device.Protocol,
            Connection = device.Connection,
            Status = device.Status
        };
    }
}
