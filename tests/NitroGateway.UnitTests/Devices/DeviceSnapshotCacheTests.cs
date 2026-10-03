using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Shared;
using NitroGateway.Storage.Configuration;
using Xunit;

namespace NitroGateway.UnitTests.Devices;

/// <summary>
/// DeviceSnapshotCache 的领域策略：scope 内经仓储加载、缓存命中、写后失效、成功刷新日志。
/// TTL/闸门等机制由 TtlCache 覆盖，此处只验证装配与领域行为。
/// </summary>
public class DeviceSnapshotCacheTests
{
    private static (DeviceSnapshotCache Cache, FakeDeviceRepository Repo, CapturingLogger Logger) Build()
    {
        var repo = new FakeDeviceRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDeviceRepository>(repo);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger();
        var cache = new DeviceSnapshotCache(
            provider.GetRequiredService<IServiceScopeFactory>(), logger);
        return (cache, repo, logger);
    }

    private static Device MakeDevice(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" },
        Connection = new DeviceConnection { Endpoint = "192.168.1.1" }
    };

    /// <summary>首次读取经仓储加载，二次读取命中缓存不再访问仓储。</summary>
    [Fact]
    public async Task GetAllAsync_LoadsOnce_ThenCaches()
    {
        var (cache, repo, _) = Build();
        repo.Devices.Add(MakeDevice("A"));

        var first = await cache.GetAllAsync();
        var second = await cache.GetAllAsync();

        Assert.True(first.IsSuccess);
        Assert.Equal("A", first.Value![0].Name);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, repo.GetAllCalls);
    }

    /// <summary>Invalidate 后下一次读取必须重新加载。</summary>
    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var (cache, repo, _) = Build();
        repo.Devices.Add(MakeDevice("A"));

        await cache.GetAllAsync();
        cache.Invalidate();
        await cache.GetAllAsync();

        Assert.Equal(2, repo.GetAllCalls);
    }

    /// <summary>仓储加载失败时按原样返回 Failure。</summary>
    [Fact]
    public async Task GetAllAsync_RepositoryFailure_ReturnsFailure()
    {
        var (cache, repo, _) = Build();
        repo.Failure = OperationalError.Storage("boom");

        var result = await cache.GetAllAsync();

        Assert.True(result.IsFailure);
        Assert.Contains("boom", result.Error!.Message);
    }

    /// <summary>加载成功记录刷新日志（IsSuccess 分支）。</summary>
    [Fact]
    public async Task GetAllAsync_LoadSuccess_LogsRefresh()
    {
        var (cache, repo, logger) = Build();
        repo.Devices.Add(MakeDevice("A"));

        await cache.GetAllAsync();

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("缓存已刷新"));
    }

    private sealed class FakeDeviceRepository : IDeviceRepository
    {
        public List<Device> Devices { get; } = [];

        public int GetAllCalls { get; private set; }

        public OperationalError? Failure { get; set; }

        public Task<OperationResult<IReadOnlyList<Device>>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(Failure is not null
                ? OperationResult<IReadOnlyList<Device>>.Failure(Failure)
                : OperationResult<IReadOnlyList<Device>>.Success(Devices));
        }

        public Task<OperationResult> SaveAsync(Device device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<Device>> GetByIdAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<Device>>> GetByStatusAsync(DeviceStatus status, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<DeviceSnapshotCache>
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
