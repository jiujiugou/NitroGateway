using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-033 阶段 2：导入服务——以中心为准重置本地：中心没有的本地设备整机移除、
/// 快照设备按 Id upsert、点位先删多余再批量导入；失败汇总不中断。
/// </summary>
public sealed class CenterConfigImporterTests
{
    [Fact]
    public async Task Import_replaces_local_with_center_snapshot()
    {
        var deviceA = TestDevices.Device("A");
        var pointA1 = TestDevices.Point("a1");
        var pointA2 = TestDevices.Point("a2");
        deviceA.AddPoint(pointA1);
        deviceA.AddPoint(pointA2);
        var deviceB = TestDevices.Device("B");

        // 同步以 Id 为键（ADR-033）：快照与本地同一设备必须同 Id
        var snapshotA = new Device
        {
            Id = deviceA.Id,
            Name = "A",
            Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
            Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
        };
        var pointA3 = TestDevices.Point("a3");
        snapshotA.AddPoint(pointA1);
        snapshotA.AddPoint(pointA3);
        var snapshotC = TestDevices.Device("C");
        var snapshot = new[] { snapshotA, snapshotC };

        var manager = new StubDeviceManager { LocalDevices = new[] { deviceA, deviceB } };
        var points = new StubPointManager();
        points.Seed(deviceA.Id, pointA1, pointA2);
        var importer = CreateImporter(manager, points);

        var result = await importer.ImportAsync(snapshot);

        Assert.True(result.IsSuccess, result.Error?.Message);
        // 中心快照没有的本地设备 B 整机移除
        Assert.Equal([deviceB.Id], manager.Unregistered);
        // 快照设备按 Id upsert（复用 RegisterAsync），保留中心 Id
        Assert.Equal([snapshotA.Id, snapshotC.Id], manager.Registered.Select(d => d.Id).ToArray());
        // 点位：本地多余 a2 移除，快照点位批量导入
        Assert.Equal([(deviceA.Id, pointA2.Id)], points.Removed);
        var imported = Assert.Single(points.Imported);
        Assert.Equal(deviceA.Id, imported.DeviceId);
        Assert.Equal(2, imported.Points.Count);
        Assert.Contains(pointA3.Id, imported.Points.Select(p => p.Id));

        var summary = result.Value!;
        Assert.Equal(2, summary.ImportedDevices);
        Assert.Equal(2, summary.ImportedPoints);
        Assert.Equal(1, summary.RemovedDevices);
        Assert.Equal(1, summary.RemovedPoints);
    }

    [Fact]
    public async Task Import_empty_snapshot_removes_all_local_devices()
    {
        var deviceA = TestDevices.Device("A");
        var deviceB = TestDevices.Device("B");
        var manager = new StubDeviceManager { LocalDevices = new[] { deviceA, deviceB } };
        var importer = CreateImporter(manager, new StubPointManager());

        var result = await importer.ImportAsync([]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([deviceA.Id, deviceB.Id], manager.Unregistered);
        Assert.Empty(manager.Registered);
        Assert.Equal(2, result.Value!.RemovedDevices);
        Assert.Equal(0, result.Value!.ImportedDevices);
    }

    [Fact]
    public async Task Import_device_failure_reports_error_and_continues()
    {
        var deviceA = TestDevices.Device("A");
        var snapshotB = TestDevices.Device("B");
        var snapshot = new[]
        {
            new Device
            {
                Id = deviceA.Id,
                Name = "A",
                Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
                Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
            },
            snapshotB
        };
        var manager = new StubDeviceManager { LocalDevices = new[] { deviceA } };
        var points = new StubPointManager();
        var importer = CreateImporter(manager, points);
        manager.FailNextRegister = true; // 第一个设备注册失败

        var result = await importer.ImportAsync(snapshot);

        Assert.True(result.IsFailure);
        Assert.Contains("导入设备", result.Error!.Message);
        // 失败不中断：第二个设备仍被导入
        var registered = Assert.Single(manager.Registered);
        Assert.Equal(snapshotB.Id, registered.Id);
    }

    private static CenterConfigImporter CreateImporter(StubDeviceManager manager, StubPointManager points)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => manager);
        services.AddScoped<IPointManager>(_ => points);
        var provider = services.BuildServiceProvider(); // 测试期存活，交由 GC 回收
        return new CenterConfigImporter(
            new StagedSnapshotCache(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CenterConfigImporter>.Instance);
    }
}
