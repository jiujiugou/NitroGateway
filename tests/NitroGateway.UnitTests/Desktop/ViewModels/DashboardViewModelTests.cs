using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services.Infrastructure;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Measurements;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;
using Xunit;

namespace NitroGateway.UnitTests.Desktop.ViewModels;

/// <summary>
/// 仪表盘页（对齐 web DashboardView）：6 个 KPI（设备总数/在线/离线或故障/总点位/今日告警/缓冲积压）
/// + 跨协议设备概览表；健康快照优先于设备自身状态；今日告警来自告警仓储计数。
/// </summary>
public sealed class DashboardViewModelTests : IDisposable
{
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    [Fact]
    public async Task Refresh_computes_six_kpis_and_cross_protocol_overview()
    {
        var cache = new StagedSnapshotCache();
        var (vm, _) = CreateVm(cache, new FakeHealth(), backlog: 7, todayAlarms: 3);

        var modbus = TestDevices.Device("PLC-1", TestDevices.Point("P1"), TestDevices.Point("P2"));
        modbus.Protocol = new ProtocolIdentifier { Name = "Modbus", Dialect = "TCP" };
        modbus.Status = DeviceStatus.Online;
        var opcUa = OpcUaDevice("UA-1");
        opcUa.Status = DeviceStatus.Offline;

        cache.EnqueueSuccess(modbus, opcUa);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(1, vm.OnlineCount);
        Assert.Equal(1, vm.OfflineCount);   // 离线或故障合并
        Assert.Equal(2, vm.TotalPoints);
        Assert.Equal(3, vm.TodayAlarmCount);
        Assert.Equal(7, vm.BufferBacklog);

        Assert.Equal(2, vm.Devices.Count);
        var first = vm.Devices.Single(d => d.Name == "PLC-1");
        Assert.Equal("Modbus (TCP)", first.Protocol);
        Assert.Equal("在线", first.StatusText);
        Assert.Equal(2, first.PointsCount);

        var second = vm.Devices.Single(d => d.Name == "UA-1");
        Assert.Equal("OPC UA", second.Protocol);          // 无方言 → 不带括号
        Assert.Equal("opc.tcp://127.0.0.1:4840", second.Endpoint);
        Assert.Equal("离线", second.StatusText);
    }

    [Fact]
    public async Task Error_status_counts_as_offline_kpi()
    {
        var cache = new StagedSnapshotCache();
        var (vm, _) = CreateVm(cache, new FakeHealth(), backlog: 0, todayAlarms: 0);

        var broken = TestDevices.Device("PLC-err");
        broken.Status = DeviceStatus.Error;
        cache.EnqueueSuccess(broken);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.OnlineCount);
        Assert.Equal(1, vm.OfflineCount);
    }

    [Fact]
    public async Task Health_snapshot_status_takes_precedence_over_device_status()
    {
        var cache = new StagedSnapshotCache();
        var health = new FakeHealth();
        var (vm, _) = CreateVm(cache, health, backlog: 0, todayAlarms: 0);

        var device = TestDevices.Device("PLC-1");
        device.Status = DeviceStatus.Online;                       // 设备自身说在线
        health.Set(device.Id, DeviceStatus.Error);                 // 健康监控说异常
        cache.EnqueueSuccess(device);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.OnlineCount);
        Assert.Equal(1, vm.OfflineCount);
        Assert.Equal("异常", Assert.Single(vm.Devices).StatusText);
    }

    [Fact]
    public async Task Refresh_device_load_failure_reports_status_without_throwing()
    {
        var cache = new StagedSnapshotCache();
        cache.Enqueue(Task.FromResult(
            OperationResult<IReadOnlyList<Device>>.Failure(OperationalError.Storage("库不可用"))));
        cache.Enqueue(Task.FromResult(
            OperationResult<IReadOnlyList<Device>>.Failure(OperationalError.Storage("库不可用"))));

        var (vm, _) = CreateVm(cache, new FakeHealth(), backlog: 0, todayAlarms: 0);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("加载设备失败", vm.StatusText);
        Assert.Empty(vm.Devices);
    }

    [Fact]
    public async Task Today_alarm_count_failure_degrades_to_zero()
    {
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(TestDevices.Device("PLC-1"));   // 构造时首次刷新
        cache.EnqueueSuccess(TestDevices.Device("PLC-1"));   // 本次刷新

        var (vm, _) = CreateVm(cache, new FakeHealth(), backlog: 0, todayAlarms: -1);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(0, vm.TodayAlarmCount);
        Assert.Equal(1, vm.TotalCount);   // 其余 KPI 不受影响
    }

    private (DashboardViewModel Vm, FakeAlarmRepository Alarms) CreateVm(
        IDeviceSnapshotCache cache, FakeHealth health, int backlog, int todayAlarms)
    {
        var alarms = new FakeAlarmRepository(todayAlarms);
        var buffer = new FakeBuffer(backlog);
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRepository>(_ => alarms);
        _provider = services.BuildServiceProvider();

        var vm = new DashboardViewModel(
            cache, health, _provider.GetRequiredService<IServiceScopeFactory>(), buffer,
            new UiDispatcher(), NullLogger<DashboardViewModel>.Instance);
        return (vm, alarms);
    }

    private static Device OpcUaDevice(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Protocol = new ProtocolIdentifier { Name = "OPC UA" },
        Connection = new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840" }
    };

    private sealed class FakeHealth : IDeviceHealthMonitor
    {
        private readonly Dictionary<Guid, DeviceHealthSnapshot> _snapshots = [];

        public int FailureThreshold => 3;
        public int RecoveryThreshold => 3;
        public void ReportSuccess(Guid deviceId, string? deviceName) { }
        public void ReportFailure(Guid deviceId, string? deviceName, string reason) { }
        public void UpdateStatus(Guid deviceId, DeviceStatus status) { }
        public DeviceHealthSnapshot? GetSnapshot(Guid deviceId) => _snapshots.GetValueOrDefault(deviceId);
        public IReadOnlyList<DeviceHealthSnapshot> GetAllSnapshots() => _snapshots.Values.ToList();
        public void Remove(Guid deviceId) => _snapshots.Remove(deviceId);
        public void AddListener(IDeviceHealthListener listener) { }

        public void Set(Guid deviceId, DeviceStatus status) =>
            _snapshots[deviceId] = new DeviceHealthSnapshot { DeviceId = deviceId, Status = status };
    }

    private sealed class FakeBuffer(int count) : IForwardBuffer
    {
        public int Count => count;
        public Task<int> GetCountAsync(CancellationToken ct = default) => Task.FromResult(count);
        public Task<OperationResult> EnqueueAsync(BatchMeasurements batch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<BatchMeasurements>>> DequeueAsync(int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> CommitAsync(IReadOnlyList<Guid> batchIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> MarkFailedAsync(Guid batchId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<DeadLetterEntry>>> GetDeadLettersAsync(int maxCount, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> RetryDeadLetterAsync(Guid batchId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> DiscardDeadLetterAsync(Guid batchId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> PurgeDeadLettersAsync(DateTime before, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>今日告警计数桩：负值表示仓储返回失败（用于降级断言）。</summary>
    private sealed class FakeAlarmRepository(int todayCount) : IAlarmRepository
    {
        public DateTime? LastSinceUtc { get; private set; }

        public Task<OperationResult<int>> CountOccurredSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
        {
            LastSinceUtc = sinceUtc;
            return Task.FromResult(todayCount < 0
                ? OperationResult<int>.Failure(OperationalError.Storage("统计失败"))
                : OperationResult<int>.Success(todayCount));
        }

        public Task<OperationResult> SaveAsync(NitroGateway.Alarm.Domain.Alarm alarm, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> UpdateStateAsync(Guid alarmId, NitroGateway.Alarm.Domain.AlarmState state, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> GetActiveByDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> GetAllActiveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<NitroGateway.Alarm.Domain.Alarm>>> QueryAsync(DateTime from, DateTime to, int limit = 1000, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
