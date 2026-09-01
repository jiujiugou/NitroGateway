using Microsoft.AspNetCore.Mvc;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using NitroGateway.Webapi.Controllers;
using NitroGateway.Webapi.Models;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-070 层次1：OpcUaBrowseController 行为测试。
/// 覆盖：设备不存在 → 404；非 OPC UA（能力不支持浏览）→ 400；未连接先建连 → 200；
/// 建连失败 → 400；浏览失败 → 400；浏览成功返回节点列表（DTO 映射）。
/// </summary>
public class OpcUaBrowseControllerTests
{
    private static Device Device(string protocolName = "OPC UA") => new()
    {
        Id = Guid.NewGuid(),
        Name = "dev",
        Protocol = new ProtocolIdentifier { Name = protocolName },
        Connection = new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840" }
    };

    private static BrowseNode Node(string id, string name, bool variable, string type = "", string access = "") => new()
    {
        NodeId = id,
        Name = name,
        TypeName = type,
        IsVariable = variable,
        Access = access
    };

    [Fact]
    public async Task Browse_DeviceNotFound_Returns404()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Failure(OperationalError.NotFound("设备不存在")) };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(new UnsupportedDriver()));

        var result = await ctrl.Browse(Guid.NewGuid(), null, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<BrowseNodeDto>>>(notFound.Value);
        Assert.False(body.Success);
        Assert.Equal("NotFound", body.Error?.Code);
    }

    [Fact]
    public async Task Browse_NonOpcUaProtocol_Returns400()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Success(Device("Modbus")) };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(new UnsupportedDriver()));

        var result = await ctrl.Browse(devices.Device!.Id, null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<BrowseNodeDto>>>(bad.Value);
        Assert.False(body.Success);
        Assert.Equal("Browse", body.Error?.Code);
    }

    [Fact]
    public async Task Browse_Connected_ReturnsMappedNodes()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Success(Device()) };
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Connected,
            BrowseResult = OperationResult<IReadOnlyList<BrowseNode>>.Success(new[]
            {
                Node("ns=2;i=1001", "Int32Var", true, "Int32", "ReadWrite"),
                Node("ns=2;i=5001", "Simulation", false)
            })
        };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(driver));

        var result = await ctrl.Browse(devices.Device!.Id, "ns=2;i=5001", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<BrowseNodeDto>>>(ok.Value);
        Assert.True(body.Success);
        Assert.Equal(2, body.Data!.Count);
        var intVar = body.Data[0];
        Assert.Equal("ns=2;i=1001", intVar.NodeId);
        Assert.Equal("Int32", intVar.TypeName);
        Assert.True(intVar.IsVariable);
        Assert.Equal("ReadWrite", intVar.Access);
        // 非变量节点 TypeName/Access 为空串
        Assert.False(body.Data[1].IsVariable);
        Assert.Equal("", body.Data[1].TypeName);
        Assert.Equal("", body.Data[1].Access);
        // 传入 parent 原样透传给驱动
        Assert.Equal("ns=2;i=5001", driver.LastParent);
    }

    [Fact]
    public async Task Browse_NotConnected_ConnectsFirst_ThenReturnsNodes()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Success(Device()) };
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Disconnected,
            BrowseResult = OperationResult<IReadOnlyList<BrowseNode>>.Success(Array.Empty<BrowseNode>())
        };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(driver));

        var result = await ctrl.Browse(devices.Device!.Id, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(driver.ConnectCalled, "未连接时应先建连");
        Assert.Equal("", driver.LastParent); // parent 缺省为空串 → 驱动用 Objects 根
    }

    [Fact]
    public async Task Browse_ConnectFails_Returns400()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Success(Device()) };
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Disconnected,
            ConnectResult = OperationResult.Failure(OperationalError.Communication("连接超时"))
        };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(driver));

        var result = await ctrl.Browse(devices.Device!.Id, null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<BrowseNodeDto>>>(bad.Value);
        Assert.False(body.Success);
        Assert.Contains("连接失败", body.Error?.Message);
    }

    [Fact]
    public async Task Browse_DriverBrowseFails_Returns400()
    {
        var devices = new FakeDevices { DeviceResult = OperationResult<Device>.Success(Device()) };
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Connected,
            BrowseResult = OperationResult<IReadOnlyList<BrowseNode>>.Failure(OperationalError.Protocol("OPC UA 浏览失败"))
        };
        var ctrl = new OpcUaBrowseController(devices, new FakePool(driver));

        var result = await ctrl.Browse(devices.Device!.Id, null, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var body = Assert.IsType<ApiResponse<List<BrowseNodeDto>>>(bad.Value);
        Assert.False(body.Success);
        Assert.Equal("Browse", body.Error?.Code);
        Assert.Contains("OPC UA 浏览失败", body.Error?.Message);
    }

    // ── fakes ──

    private sealed class FakeDevices : IDeviceManager
    {
        public OperationResult<Device> DeviceResult { get; init; } =
            OperationResult<Device>.Failure(OperationalError.NotFound("设备不存在"));

        public Device? Device => DeviceResult.IsSuccess ? DeviceResult.Value : null;

        public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default)
            => Task.FromResult(OperationResult<Device>.Success(device));
        public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult(DeviceResult);
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default)
            => Task.FromResult<OperationResult<IReadOnlyList<Device>>>(Array.Empty<Device>());
        public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult(DeviceResult);
        public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
    }

    private sealed class FakePool(IProtocolDriver driver) : IProtocolDriverPool
    {
        public IProtocolDriver GetOrCreate(Device device) => driver;
        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    /// <summary>不支持浏览的驱动（如 Modbus/S7 假驱动）：Capability.SupportsBrowse=false 且非 IBrowseableDriver</summary>
    private sealed class UnsupportedDriver : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();
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
    }

    private sealed class FakeBrowseDriver : IProtocolDriver, IBrowseableDriver
    {
        public DriverState State { get; set; } = DriverState.Connected;
        public DriverCapability Capability { get; } = new() { SupportsBrowse = true };
        public OperationResult ConnectResult { get; init; } = OperationResult.Success();
        public OperationResult<IReadOnlyList<BrowseNode>> BrowseResult { get; init; } =
            OperationResult<IReadOnlyList<BrowseNode>>.Success(Array.Empty<BrowseNode>());
        public bool ConnectCalled { get; private set; }
        public string? LastParent { get; private set; }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            ConnectCalled = true;
            return Task.FromResult(ConnectResult);
        }
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
        public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default)
        {
            LastParent = parentNodeId;
            return Task.FromResult(BrowseResult);
        }
        public void Dispose() { }
    }
}
