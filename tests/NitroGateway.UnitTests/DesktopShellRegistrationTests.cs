using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.UnitTests;

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


