using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027 P2-2：AlarmsViewModel 为单例，IAlarmRepository 为 Scoped（EF DbContext），
/// 每次刷新必须新建 scope 解析仓储并在刷新结束释放，防止 change tracker 跨轮询累积。
/// </summary>
public sealed class AlarmsViewModelTests
{
    [Fact]
    public async Task RefreshAsync_resolves_scoped_repository_per_refresh_and_disposes_scope()
    {
        var created = new List<TrackingAlarmRepository>();
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRepository>(_ =>
        {
            var repo = new TrackingAlarmRepository();
            created.Add(repo);
            return repo;
        });
        using var provider = services.BuildServiceProvider();

        var vm = new AlarmsViewModel(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StagedSnapshotCache(),
            new UiDispatcher(),
            NullLogger<AlarmsViewModel>.Instance);

        // 构造时的首次刷新已同步完成，其 scope 仓储已释放
        Assert.Single(created);
        Assert.True(created[0].Disposed);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, created.Count);
        Assert.NotSame(created[0], created[1]);
        Assert.True(created[1].Disposed);
    }

    private sealed class TrackingAlarmRepository : IAlarmRepository, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public Task<OperationResult> SaveAsync(NitroGateway.Alarm.Domain.Alarm alarm, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UpdateStateAsync(Guid alarmId, NitroGateway.Alarm.Domain.AlarmState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> GetActiveByDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>.Success(Array.Empty<NitroGateway.Alarm.Domain.Alarm>()));
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> QueryAsync(DateTime from, DateTime to, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>.Success(Array.Empty<NitroGateway.Alarm.Domain.Alarm>()));
    }
}
