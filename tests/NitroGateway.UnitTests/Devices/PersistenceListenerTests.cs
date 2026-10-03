using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.DeviceManagement.Listeners;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests.Devices;

/// <summary>
/// PersistenceListener：HealthMonitor 状态变更 → 通过新 scope 取 IDeviceManager 落库。
/// 覆盖成功转发、失败结果记错误日志、依赖异常被吞掉不冒泡。
/// </summary>
public class PersistenceListenerTests
{
    private static (PersistenceListener Listener, FakeDeviceManager Manager, CapturingLogger Logger) Build(
        FakeDeviceManager? manager = null)
    {
        manager ??= new FakeDeviceManager();
        var services = new ServiceCollection();
        services.AddSingleton<IDeviceManager>(manager);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger();
        var listener = new PersistenceListener(
            provider.GetRequiredService<IServiceScopeFactory>(), logger);
        return (listener, manager, logger);
    }

    private static DeviceHealthChanged Event(Guid id, DeviceStatus status) => new()
    {
        DeviceId = id,
        DeviceName = "PLC",
        OldStatus = DeviceStatus.Online,
        NewStatus = status
    };

    /// <summary>收到事件后把新状态转发给 IDeviceManager.UpdateStatusAsync。</summary>
    [Fact]
    public async Task OnHealthChanged_ForwardsStatusToManager()
    {
        var id = Guid.NewGuid();
        var (listener, manager, _) = Build();

        await listener.OnHealthChangedAsync(Event(id, DeviceStatus.Offline));

        Assert.Equal((id, DeviceStatus.Offline), Assert.Single(manager.StatusUpdates));
    }

    /// <summary>落库返回失败时应记错误日志（IsFailure 分支）。</summary>
    [Fact]
    public async Task OnHealthChanged_ManagerFailure_LogsError()
    {
        var manager = new FakeDeviceManager
        {
            Result = OperationResult.Failure(OperationalError.Storage("磁盘满"))
        };
        var (listener, _, logger) = Build(manager);

        await listener.OnHealthChangedAsync(Event(Guid.NewGuid(), DeviceStatus.Offline));

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("持久化失败"));
    }

    /// <summary>依赖抛异常时应被吞掉并记日志，绝不向调用方传播。</summary>
    [Fact]
    public async Task OnHealthChanged_ManagerThrows_SwallowedAndLogged()
    {
        var manager = new FakeDeviceManager { Throw = true };
        var (listener, _, logger) = Build(manager);

        await listener.OnHealthChangedAsync(Event(Guid.NewGuid(), DeviceStatus.Offline));

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("持久化异常"));
    }

    private sealed class FakeDeviceManager : IDeviceManager
    {
        public List<(Guid Id, DeviceStatus Status)> StatusUpdates { get; } = [];

        public OperationResult Result { get; set; } = OperationResult.Success();

        public bool Throw { get; set; }

        public Task<OperationResult> UpdateStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("boom");
            StatusUpdates.Add((deviceId, status));
            return Task.FromResult(Result);
        }

        public Task<OperationResult<Device>> RegisterAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UnregisterAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(string? siteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(string? siteId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetAllIncludingDeletedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetIncludingDeletedAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> SoftDeleteAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> SetMaintenanceAsync(Guid deviceId, bool maintenance, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<PersistenceListener>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
