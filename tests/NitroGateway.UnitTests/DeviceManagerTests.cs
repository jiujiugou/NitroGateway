using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// 设备管理器单元测试——FakeDeviceRepository 模拟数据层。
///
/// <para>DeviceManager 的核心业务逻辑不是 CRUD（那由 Repository 负责），而是：
/// 1. 状态门控：不允许从 Offline 手动切换到 Online（必须通过 DeviceHealthMonitor 恢复）
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

    public DeviceManagerTests()
    {
        _manager = new DeviceManager(_repo, NullLogger<DeviceManager>.Instance);
    }

    /// <summary>正常注册设备，ID + Name 正确返回。</summary>
    [Fact]
    public async Task RegisterAsync_CreatesDevice()
    {
        var device = MakeDevice("PLC01");
        var result = await _manager.RegisterAsync(device);
        Assert.True(result.IsSuccess);
        Assert.Equal("PLC01", result.Value!.Name);
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

    /// <summary>
    /// 状态门控：Offline → Online 必须由 HealthMonitor 触发，手动调用应被拒绝。
    /// 这是防止运维人员误操作的关键保护——设备离线后只有自动恢复机制能将其标记为上线。
    /// </summary>
    [Fact]
    public async Task UpdateStatus_OfflineToOnline_ManuallyRejected()
    {
        var id = Guid.NewGuid();
        _repo.Devices[id] = MakeDevice("PLC", status: DeviceStatus.Offline);
        var result = await _manager.UpdateStatusAsync(id, DeviceStatus.Online);
        Assert.False(result.IsSuccess);
        Assert.Contains("HealthMonitor", result.Error!.Message);
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

    /// <summary>获取不存在的设备应返回 Failure。</summary>
    [Fact]
    public async Task GetAsync_NonExistentDevice_ReturnsFailure()
    {
        var result = await _manager.GetAsync(Guid.NewGuid());
        Assert.False(result.IsSuccess);
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

        public Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default)
        {
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
}
