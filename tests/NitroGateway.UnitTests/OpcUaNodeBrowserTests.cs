using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-070 层次1（桌面端）：<see cref="OpcUaNodeBrowser"/> 与 Webapi <c>OpcUaBrowseController</c> 同语义的
/// 进程内浏览。覆盖：设备不存在；协议不支持浏览（不建连即拒绝）；未连接先建连；建连失败/浏览失败透传；
/// parent 原样下传；驱动抛异常归类为 General 不冒泡。
/// </summary>
public sealed class OpcUaNodeBrowserTests : IDisposable
{
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    [Fact]
    public async Task Browse_device_not_found_returns_failure()
    {
        var browser = Create(device: null, new FakeBrowseDriver());

        var result = await browser.BrowseAsync(Guid.NewGuid(), "");

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Browse_unsupported_protocol_without_browse_capability_fails_without_connecting()
    {
        var driver = new UnsupportedDriver();
        var browser = Create(Device("Modbus"), driver);

        var result = await browser.BrowseAsync(Guid.NewGuid(), "");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Validation, result.Error!.Category);
        Assert.False(driver.ConnectCalled);
    }

    [Fact]
    public async Task Browse_disconnected_connects_first_then_browses()
    {
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Disconnected,
            BrowseResult = OperationResult<IReadOnlyList<BrowseNode>>.Success([Node("ns=2;i=1001", "温度", true, "Double", "Read")])
        };
        var browser = Create(Device(), driver);

        var result = await browser.BrowseAsync(Guid.NewGuid(), "ns=2;i=5001");

        Assert.True(result.IsSuccess);
        Assert.True(driver.ConnectCalled);
        Assert.Equal("ns=2;i=5001", driver.LastParent);
        var node = Assert.Single(result.Value!);
        Assert.Equal("ns=2;i=1001", node.NodeId);
        Assert.True(node.IsVariable);
    }

    [Fact]
    public async Task Browse_connect_failure_is_propagated()
    {
        var driver = new FakeBrowseDriver
        {
            State = DriverState.Disconnected,
            ConnectResult = OperationResult.Failure(OperationalError.Communication("连接超时"))
        };
        var browser = Create(Device(), driver);

        var result = await browser.BrowseAsync(Guid.NewGuid(), "");

        Assert.True(result.IsFailure);
        Assert.Contains("连接超时", result.Error!.Message);
    }

    [Fact]
    public async Task Browse_driver_failure_is_propagated()
    {
        var driver = new FakeBrowseDriver
        {
            BrowseResult = OperationResult<IReadOnlyList<BrowseNode>>.Failure(OperationalError.Protocol("OPC UA 浏览失败"))
        };
        var browser = Create(Device(), driver);

        var result = await browser.BrowseAsync(Guid.NewGuid(), "");

        Assert.True(result.IsFailure);
        Assert.Contains("OPC UA 浏览失败", result.Error!.Message);
    }

    [Fact]
    public async Task Browse_driver_exception_is_classified_not_thrown()
    {
        var driver = new FakeBrowseDriver { ThrowOnBrowse = new InvalidOperationException("会话已释放") };
        var browser = Create(Device(), driver);

        var result = await browser.BrowseAsync(Guid.NewGuid(), "");

        Assert.True(result.IsFailure);
        Assert.Contains("浏览异常", result.Error!.Message);
    }

    // ── helpers / fakes ──

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

    /// <summary>浏览器按单例使用：构造函数取 IServiceScopeFactory，故测试用根容器提供的工厂（容器由测试持有）。</summary>
    private OpcUaNodeBrowser Create(Device? device, IProtocolDriver driver)
    {
        var manager = new StubDeviceManager { GetResult = device };
        var services = new ServiceCollection();
        services.AddScoped<IDeviceManager>(_ => manager);
        _provider = services.BuildServiceProvider();

        return new OpcUaNodeBrowser(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FakePool(driver),
            NullLogger<OpcUaNodeBrowser>.Instance);
    }

    private sealed class FakePool(IProtocolDriver driver) : IProtocolDriverPool
    {
        public IProtocolDriver GetOrCreate(Device device) => driver;
        public void Evict(Guid deviceId) { }
        public void Dispose() { }
    }

    /// <summary>不支持浏览的驱动（Modbus/S7 假驱动）：SupportsBrowse=false 且未实现 IBrowseableDriver。</summary>
    private sealed class UnsupportedDriver : IProtocolDriver
    {
        public DriverState State => DriverState.Connected;
        public DriverCapability Capability => new();
        public bool ConnectCalled { get; private set; }

        public Task<OperationResult> ConnectAsync(CancellationToken ct = default)
        {
            ConnectCalled = true;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DisconnectAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PingAsync(CancellationToken ct = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult<RawPointValue>> ReadAsync(DevicePoint point, CancellationToken ct = default)
            => Task.FromResult(OperationResult<RawPointValue>.Failure(OperationalError.Protocol("不支持")));
        public Task<OperationResult<IReadOnlyList<RawPointValue>>> ReadBatchAsync(IEnumerable<DevicePoint> points, CancellationToken ct = default)
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
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
        public Exception? ThrowOnBrowse { get; init; }
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
            => Task.FromResult(OperationResult<IReadOnlyList<RawPointValue>>.Success(Array.Empty<RawPointValue>()));
        public Task<OperationResult> WriteAsync(DevicePoint point, object value, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> WriteBatchAsync(IEnumerable<KeyValuePair<DevicePoint, object>> entries, CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public Task<OperationResult<IReadOnlyList<BrowseNode>>> BrowseAsync(string parentNodeId = "", CancellationToken ct = default)
        {
            LastParent = parentNodeId;
            if (ThrowOnBrowse is not null)
                throw ThrowOnBrowse;
            return Task.FromResult(BrowseResult);
        }

        public void Dispose() { }
    }
}
