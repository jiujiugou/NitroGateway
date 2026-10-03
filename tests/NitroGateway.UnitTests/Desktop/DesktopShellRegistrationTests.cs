using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services.Connectivity;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.Services.Sync;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests.Desktop;

/// <summary>ADR-026：桌面壳 DI 注册——EventBridge 同时接入三类事件通道。</summary>
public sealed class DesktopShellRegistrationTests
{
    [Fact]
    public void AddNitroDesktopShell_wires_event_bridge_as_all_listeners()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(new StubForwardBuffer());
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<MqttConnectionOptions>();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddNitroDesktopShell(configuration);

        using var provider = services.BuildServiceProvider();
        var bridge = provider.GetRequiredService<EventBridge>();

        Assert.Same(bridge, provider.GetRequiredService<IPointStoredSink>());
        Assert.Same(bridge, provider.GetRequiredService<IDeviceHealthListener>());
        Assert.Same(bridge, provider.GetRequiredService<IMqttStateListener>());
    }

    [Fact]
    public void PointsViewModelFactory_is_registered_and_resolves()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(new StubForwardBuffer());
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<MqttConnectionOptions>();
        // ADR-033：ConfigSyncOutboxStore 需要连接串（PointsViewModelFactory 的构造函数依赖）
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:ConnectionString"] = "Data Source=:memory:"
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddNitroDesktopShell(configuration);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IPointsViewModelFactory>();

        Assert.NotNull(factory);
        Assert.IsType<PointsViewModelFactory>(factory);
    }

    /// <summary>
    /// 协议分区启动路径：设备列表工厂 + OPC UA 浏览 + 对话框服务在真实 DI 图上可解析（均为单例，不得捕获 Scoped）。
    /// 这是 MainViewModel 构造的前半段，回归「新增注册遗漏/生命周期错配」导致的应用启动崩溃。
    /// </summary>
    [Fact]
    public void DeviceListFactory_and_opcua_node_browser_resolve_from_shell_graph()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IForwardBuffer>(new StubForwardBuffer());
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<MqttConnectionOptions>();
        services.AddSingleton<IDeviceSnapshotCache>(new StagedSnapshotCache());
        services.AddSingleton<IDeviceHealthMonitor>(new StubHealthMonitor());
        // 与 GatewayHost 一致：驱动工厂/驱动池由 AddNitroProtocol 提供（DeviceConnectionTester 依赖 IProtocolDriverFactory）
        services.AddNitroProtocol();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:ConnectionString"] = "Data Source=:memory:"
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddNitroDesktopShell(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IOpcUaNodeBrowser>());
        Assert.NotNull(provider.GetRequiredService<IDeviceDialogService>());

        var factory = provider.GetRequiredService<IDevicesViewModelFactory>();
        var generic = factory.Create(DeviceListScope.Generic);
        var opcUa = factory.Create(DeviceListScope.OpcUa);
        Assert.Equal("Modbus / S7 设备", generic.Scope.Title);
        Assert.True(opcUa.Scope.OpcUaOnly);

        generic.Dispose();
        opcUa.Dispose();
    }

    private sealed class StubForwardBuffer : IForwardBuffer
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
}


