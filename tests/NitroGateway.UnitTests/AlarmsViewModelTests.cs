using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
// 命名空间 NitroGateway.Alarm 与告警实体同名冲突，实体类型用别名引用
using AlarmEntity = NitroGateway.Alarm.Domain.Alarm;
using NitroGateway.Desktop.Services.Infrastructure;
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

    // ===== ADR-037 S7：增量刷新保留行实例/顺序 =====

    [Fact]
    public async Task Refresh_reuses_row_instances_and_preserves_order()
    {
        var repo = new StubAlarmRepository
        {
            History = { TestAlarm("A1", DateTime.UtcNow.AddMinutes(-5)), TestAlarm("A2", DateTime.UtcNow.AddMinutes(-1)) }
        };
        var (vm, _) = CreateVm(repo);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("A2", vm.Items[0].Message);
        var top = vm.Items[0];
        var bottom = vm.Items[1];

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Same(top, vm.Items[0]);
        Assert.Same(bottom, vm.Items[1]);
    }

    [Fact]
    public async Task Refresh_updates_state_in_place_and_inserts_new_alarm_at_top()
    {
        var a1 = TestAlarm("A1", DateTime.UtcNow.AddMinutes(-5));
        var repo = new StubAlarmRepository { History = { a1 } };
        var (vm, _) = CreateVm(repo);

        await vm.RefreshCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Items);
        a1.State = AlarmState.Acknowledged;
        repo.History.Add(TestAlarm("A2", DateTime.UtcNow));

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Same(row, vm.Items[1]);
        Assert.Equal("已确认", row.StateText);
        Assert.Equal("A2", vm.Items[0].Message);
    }

    [Fact]
    public async Task Refresh_removes_gone_alarm()
    {
        var a1 = TestAlarm("A1", DateTime.UtcNow.AddMinutes(-5));
        var a2 = TestAlarm("A2", DateTime.UtcNow.AddMinutes(-1));
        var repo = new StubAlarmRepository { History = { a1, a2 } };
        var (vm, _) = CreateVm(repo);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        repo.History.Remove(a2);

        await vm.RefreshCommand.ExecuteAsync(null);

        var survivor = Assert.Single(vm.Items);
        Assert.Equal(a1.Id, survivor.Id);
    }

    [Fact]
    public async Task Timer_tick_triggers_refresh()
    {
        // 轮询节奏经 IUiTimer 注入：FakeUiTimer 手动触发一个周期等价 DispatcherTimer 到达
        var repo = new StubAlarmRepository();
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRepository>(_ => repo);
        using var provider = services.BuildServiceProvider();
        var timer = new FakeUiTimer();
        var vm = new AlarmsViewModel(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StagedSnapshotCache(),
            new UiDispatcher(),
            NullLogger<AlarmsViewModel>.Instance,
            timer);

        Assert.True(timer.IsStarted);
        Assert.Equal(1, timer.StartCalls);

        repo.History.Add(TestAlarm("A1", DateTime.UtcNow.AddMinutes(-1)));
        timer.RaiseTick();

        await TestWait.UntilAsync(() => vm.Items.Count == 1);
        Assert.Equal("A1", vm.Items[0].Message);
    }

    private static (AlarmsViewModel Vm, ServiceProvider Provider) CreateVm(StubAlarmRepository repo)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRepository>(_ => repo);
        var provider = services.BuildServiceProvider();
        var vm = new AlarmsViewModel(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StagedSnapshotCache(),
            new UiDispatcher(),
            NullLogger<AlarmsViewModel>.Instance);
        return (vm, provider);
    }

    private static AlarmEntity TestAlarm(string message, DateTime occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        RuleId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        PointId = Guid.NewGuid(),
        Message = message,
        Severity = AlarmSeverity.Warning,
        TriggerValue = 1,
        Threshold = 2,
        OccurredAt = occurredAt
    };

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
        public Task<OperationResult<int>> CountOccurredSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<int>.Success(0));
    }
    /// <summary>可编程告警仓储：QueryAsync 返回 History、GetAllActiveAsync 返回 Active。</summary>
    private sealed class StubAlarmRepository : IAlarmRepository
    {
        public List<AlarmEntity> History { get; } = [];
        public List<AlarmEntity> Active { get; } = [];

        public Task<OperationResult<IReadOnlyList<AlarmEntity>>> QueryAsync(
            DateTime from, DateTime to, int limit = 1000, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<AlarmEntity>>.Success(History));

        public Task<OperationResult<IReadOnlyList<AlarmEntity>>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<AlarmEntity>>.Success(Active));

        public Task<OperationResult> SaveAsync(AlarmEntity alarm, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UpdateStateAsync(Guid alarmId, AlarmState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmEntity>>> GetActiveByDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<int>> CountOccurredSinceAsync(DateTime sinceUtc, CancellationToken ct = default) =>
            Task.FromResult(OperationResult<int>.Success(0));
    }
}
