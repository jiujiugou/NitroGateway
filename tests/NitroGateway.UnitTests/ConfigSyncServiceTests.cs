using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Webapi.Models;
using NitroGateway.Webapi.Services;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-033 阶段 3/4 中心侧合并：tombstone 拒绝现场复活、中心较新整台跳过、
/// 点位级 UpdatedAt 合并（现场删除 → 中心 tombstone）、设备删除上报幂等软删。
/// </summary>
public sealed class ConfigSyncServiceTests
{
    private readonly StubDeviceManager _devices = new();
    private readonly StubPointManager _points = new();

    [Fact]
    public async Task Center_tombstone_rejects_local_resurrection()
    {
        var deviceId = Guid.NewGuid();
        _devices.GetResult = Device(deviceId, "已删设备", IsDeleted: true);
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            Changes =
            [
                new ConfigSyncChangeDto
                {
                    Device = DeviceDto(deviceId, "现场复活", updatedAt: DateTime.UtcNow.ToString("O")),
                    Deleted = false
                }
            ]
        });

        Assert.Equal("rejected", Assert.Single(result.Results!).Action);
        Assert.Empty(_devices.Registered);
    }

    [Fact]
    public async Task Center_newer_skips_whole_device()
    {
        var deviceId = Guid.NewGuid();
        _devices.GetResult = Device(deviceId, "中心新版本", UpdatedAt: DateTime.UtcNow);
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            Changes =
            [
                new ConfigSyncChangeDto
                {
                    Device = DeviceDto(deviceId, "现场旧版本", updatedAt: DateTime.UtcNow.AddHours(-1).ToString("O")),
                    Deleted = false
                }
            ]
        });

        Assert.Equal("skipped", Assert.Single(result.Results!).Action);
        Assert.Empty(_devices.Registered);
    }

    [Fact]
    public async Task Accepted_push_tombstones_center_points_and_applies_device()
    {
        var deviceId = Guid.NewGuid();
        var point = TestDevices.Point("被现场删除的点位");
        point.UpdatedAt = DateTime.UtcNow.AddHours(-2);
        var existing = Device(deviceId, "PLC-1", UpdatedAt: DateTime.UtcNow.AddHours(-2));
        existing.AddPoint(point);
        _devices.GetResult = existing;
        _points.Seed(deviceId, point);
        var incomingAt = DateTime.UtcNow.AddMinutes(-5);
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            Changes =
            [
                new ConfigSyncChangeDto
                {
                    Device = DeviceDto(deviceId, "PLC-1", updatedAt: incomingAt.ToString("O")),
                    DeletedPointIds = [point.Id.ToString()],
                    Deleted = false
                }
            ]
        });

        Assert.Equal("accepted", Assert.Single(result.Results!).Action);
        // 设备按现场版本落库（保留现场时间戳）
        var registered = Assert.Single(_devices.Registered);
        Assert.Equal(deviceId, registered.Id);
        // 现场删除的点位 → 中心 tombstone（IsDeleted=true，时间取 max）
        var imported = Assert.Single(_points.Imported);
        var tombstoned = Assert.Single(imported.Points);
        Assert.True(tombstoned.IsDeleted);
        Assert.Equal(incomingAt, tombstoned.UpdatedAt);
    }

    [Fact]
    public async Task Device_tombstone_push_soft_deletes_existing()
    {
        var deviceId = Guid.NewGuid();
        _devices.GetResult = Device(deviceId, "现场删除");
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            Changes = [new ConfigSyncChangeDto { DeviceId = deviceId.ToString(), Deleted = true }]
        });

        Assert.Equal("accepted", Assert.Single(result.Results!).Action);
        Assert.Equal(deviceId, Assert.Single(_devices.Unregistered));
    }

    [Fact]
    public async Task Device_tombstone_push_for_unknown_device_is_idempotent_accepted()
    {
        var deviceId = Guid.NewGuid();
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            Changes = [new ConfigSyncChangeDto { DeviceId = deviceId.ToString(), Deleted = true }]
        });

        Assert.Equal("accepted", Assert.Single(result.Results!).Action);
    }

    [Fact]
    public async Task Push_records_device_site_ownership_from_requester()
    {
        // ADR-035 方案 A：设备归属 = 上报方站点（中心 upsert 时写入 SiteId）
        var deviceId = Guid.NewGuid();
        var service = CreateService();

        var result = await service.ApplyAsync(new ConfigSyncPushRequest
        {
            SiteId = "site-a",
            Changes =
            [
                new ConfigSyncChangeDto
                {
                    Device = DeviceDto(deviceId, "现场新增设备", updatedAt: DateTime.UtcNow.ToString("O")),
                    Deleted = false
                }
            ]
        });

        Assert.Equal("accepted", Assert.Single(result.Results!).Action);
        var saved = Assert.Single(_devices.Registered);
        Assert.Equal(deviceId, saved.Id);
        Assert.Equal("site-a", saved.SiteId);
    }
    private ConfigSyncService CreateService() => new(_devices, _points);

    private static Device Device(Guid id, string name, DateTime? UpdatedAt = null, bool IsDeleted = false)
    {
        var device = TestDevices.Device(name);
        return new Device
        {
            Id = id,
            Name = device.Name,
            Description = device.Description,
            Protocol = device.Protocol,
            Connection = device.Connection,
            Status = device.Status,
            UpdatedAt = UpdatedAt ?? default,
            IsDeleted = IsDeleted
        };
    }

    private static DeviceDto DeviceDto(Guid id, string name, string updatedAt) => new()
    {
        Id = id.ToString(),
        Name = name,
        Protocol = new ProtocolDto { Name = "Modbus", Dialect = "TCP" },
        Connection = new ConnectionDto { Endpoint = "192.168.1.10:502" },
        Status = DeviceStatus.Unknown.ToString(),
        UpdatedAt = updatedAt
    };
}

