using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services.Dialogs;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-043：告警规则管理页 ViewModel——展示全量规则（含禁用）、
/// 设备/点位名称映射与短 ID 回退、新增/编辑/删除走模态对话框，
/// 每次操作经 IServiceScopeFactory 新建 scope 解析 Scoped 仓储（与 AlarmsViewModel 同模式）。
/// </summary>
public sealed class AlarmRulesViewModelTests
{
    [Fact]
    public async Task RefreshAsync_resolves_scoped_repository_per_operation_and_disposes_scope()
    {
        var created = new List<TrackingAlarmRuleRepository>();
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRuleRepository>(_ =>
        {
            var repo = new TrackingAlarmRuleRepository();
            created.Add(repo);
            return repo;
        });
        using var provider = services.BuildServiceProvider();

        var vm = new AlarmRulesViewModel(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StagedSnapshotCache(),
            new StubAlarmRuleDialogService(),
            NullLogger<AlarmRulesViewModel>.Instance);

        // 构造时首次刷新已同步完成，其 scope 仓储已释放
        Assert.Single(created);
        Assert.True(created[0].Disposed);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, created.Count);
        Assert.NotSame(created[0], created[1]);
        Assert.True(created[1].Disposed);
    }

    [Fact]
    public void RefreshAsync_loads_all_rules_including_disabled_and_maps_names()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        d1.AddPoint(TestDevices.Point("P2"));

        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1);

        var repo = new StubAlarmRuleRepository
        {
            Rules =
            {
                TestRule(d1, p1.Id, enabled: true, severity: AlarmSeverity.Critical, @operator: ">", threshold: 80),
                TestRule(d1, p1.Id, enabled: false, severity: AlarmSeverity.Warning, @operator: "Between", threshold: 10, thresholdUpper: 50)
            }
        };
        var (vm, _, _) = CreateVm(repo, cache);

        Assert.Equal(2, vm.Items.Count);

        var row1 = vm.Items[0];
        Assert.Equal("D1", row1.DeviceName);
        Assert.Equal("P1", row1.PointName);
        Assert.Equal("> 80", row1.Condition);
        Assert.Equal("Critical", row1.SeverityText);
        Assert.Equal("启用", row1.EnabledText);
        Assert.True(row1.Enabled);

        var row2 = vm.Items[1];
        Assert.Equal("Between 10 ~ 50", row2.Condition);
        Assert.Equal("禁用", row2.EnabledText);
        Assert.False(row2.Enabled);

        Assert.Contains("2 条规则（含禁用）", vm.StatusText);
    }

    [Fact]
    public void RefreshAsync_falls_back_to_short_id_when_device_not_in_cache()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var rule = TestRule(d1, d1.Points.First().Id);

        // 设备目录缓存为空：规则携带的 Guid 无法映射名称，回退短 ID
        var repo = new StubAlarmRuleRepository { Rules = { rule } };
        var (vm, _, _) = CreateVm(repo, new StagedSnapshotCache());

        var row = Assert.Single(vm.Items);
        Assert.Equal(rule.DeviceId.ToString("N")[..8], row.DeviceName);
        Assert.Equal(rule.PointId.ToString("N")[..8], row.PointName);
    }

    [Fact]
    public void RefreshAsync_reports_failure_in_status()
    {
        var repo = new StubAlarmRuleRepository { FailGetAll = true };
        var (vm, _, _) = CreateVm(repo, new StagedSnapshotCache());

        Assert.Empty(vm.Items);
        Assert.Contains("加载告警规则失败", vm.StatusText);
    }

    [Fact]
    public async Task AddRule_saves_rule_when_dialog_confirmed()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1); // 构造刷新
        cache.EnqueueSuccess(d1); // AddRule 读设备
        cache.EnqueueSuccess(d1); // 保存后刷新

        var dialogs = new StubAlarmRuleDialogService
        {
            EditRuleResult = true,
            OnEditRule = e =>
            {
                e.DeviceId = d1.Id;
                e.PointId = p1.Id;
                e.Operator = ">";
                e.Threshold = 85;
                e.DurationSeconds = 5;
                e.Severity = AlarmSeverity.Emergency;
                e.MessageTemplate = "{value} 高";
            }
        };
        var repo = new StubAlarmRuleRepository();
        var (vm, _, _) = CreateVm(repo, cache, dialogs);

        await vm.AddRuleCommand.ExecuteAsync(null);

        var saved = Assert.Single(repo.SavedRules);
        Assert.Equal(d1.Id, saved.DeviceId);
        Assert.Equal(p1.Id, saved.PointId);
        Assert.Equal(">", saved.Operator);
        Assert.Equal(85, saved.Threshold);
        Assert.Equal(5, saved.DurationSeconds);
        Assert.Equal(AlarmSeverity.Emergency, saved.Severity);
        Assert.Equal("{value} 高", saved.MessageTemplate);
        // 保存成功后自动刷新：列表已包含新规则
        Assert.Single(vm.Items);
        Assert.Equal("D1", vm.Items[0].DeviceName);
        Assert.Contains("共 1 条规则", vm.StatusText);
    }

    [Fact]
    public async Task AddRule_skips_save_when_dialog_cancelled()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1);
        cache.EnqueueSuccess(d1);

        var dialogs = new StubAlarmRuleDialogService { EditRuleResult = false };
        var repo = new StubAlarmRuleRepository();
        var (vm, _, _) = CreateVm(repo, cache, dialogs);

        await vm.AddRuleCommand.ExecuteAsync(null);

        Assert.Empty(repo.SavedRules);
    }

    [Fact]
    public async Task AddRule_reports_when_no_devices()
    {
        var cache = new StagedSnapshotCache(); // 空设备目录
        var repo = new StubAlarmRuleRepository();
        var (vm, _, _) = CreateVm(repo, cache);

        await vm.AddRuleCommand.ExecuteAsync(null);

        Assert.Empty(repo.SavedRules);
        Assert.Contains("暂无设备", vm.StatusText);
    }

    [Fact]
    public async Task EditRule_loads_full_rule_and_saves()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        var rule = TestRule(d1, p1.Id, enabled: true, severity: AlarmSeverity.Warning, @operator: ">=", threshold: 100);

        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1); // 构造刷新
        cache.EnqueueSuccess(d1); // EditRule 读设备
        cache.EnqueueSuccess(d1); // 保存后刷新

        var dialogs = new StubAlarmRuleDialogService
        {
            EditRuleResult = true,
            OnEditRule = e => e.Threshold = 120
        };
        var repo = new StubAlarmRuleRepository { Rules = { rule } };
        var (vm, _, _) = CreateVm(repo, cache, dialogs);

        vm.SelectedRule = vm.Items[0];
        await vm.EditRuleCommand.ExecuteAsync(null);

        var saved = Assert.Single(repo.SavedRules);
        Assert.Equal(rule.Id, saved.Id);
        Assert.Equal(120, saved.Threshold);
        Assert.Equal(d1.Id, saved.DeviceId);
        Assert.Single(vm.Items);
        Assert.Contains("共 1 条规则", vm.StatusText);
    }

    [Fact]
    public async Task DeleteRule_confirms_and_deletes()
    {
        var d1 = TestDevices.Device("D1");
        var p1 = TestDevices.Point("P1");
        d1.AddPoint(p1);
        var rule = TestRule(d1, p1.Id);

        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1); // 构造刷新
        cache.EnqueueSuccess(d1); // 删除后刷新

        var dialogs = new StubAlarmRuleDialogService { ConfirmResult = true };
        var repo = new StubAlarmRuleRepository { Rules = { rule } };
        var (vm, _, _) = CreateVm(repo, cache, dialogs);

        vm.SelectedRule = vm.Items[0];
        await vm.DeleteRuleCommand.ExecuteAsync(null);

        var deleted = Assert.Single(repo.DeletedRules);
        Assert.Equal(rule.Id, deleted);
        var confirm = Assert.Single(dialogs.Confirmations);
        Assert.Contains("删除告警规则", confirm.Title);
        // 删除成功后自动刷新：列表已清空
        Assert.Empty(vm.Items);
        Assert.Contains("共 0 条规则", vm.StatusText);
    }

    [Fact]
    public async Task DeleteRule_skips_when_not_confirmed()
    {
        var d1 = TestDevices.Device("D1");
        d1.AddPoint(TestDevices.Point("P1"));
        var rule = TestRule(d1, d1.Points.First().Id);

        var cache = new StagedSnapshotCache();
        cache.EnqueueSuccess(d1);

        var dialogs = new StubAlarmRuleDialogService { ConfirmResult = false };
        var repo = new StubAlarmRuleRepository { Rules = { rule } };
        var (vm, _, _) = CreateVm(repo, cache, dialogs);

        vm.SelectedRule = vm.Items[0];
        await vm.DeleteRuleCommand.ExecuteAsync(null);

        Assert.Empty(repo.DeletedRules);
    }

    private static (AlarmRulesViewModel Vm, ServiceProvider Provider, StubAlarmRuleDialogService Dialogs) CreateVm(
        StubAlarmRuleRepository repo, StagedSnapshotCache cache, StubAlarmRuleDialogService? dialogs = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAlarmRuleRepository>(_ => repo);
        var provider = services.BuildServiceProvider();
        var d = dialogs ?? new StubAlarmRuleDialogService();
        var vm = new AlarmRulesViewModel(
            provider.GetRequiredService<IServiceScopeFactory>(),
            cache,
            d,
            NullLogger<AlarmRulesViewModel>.Instance);
        return (vm, provider, d);
    }

    private static AlarmRule TestRule(
        Device device,
        Guid pointId,
        bool enabled = true,
        AlarmSeverity severity = AlarmSeverity.Warning,
        string @operator = ">",
        double threshold = 80,
        double? thresholdUpper = null) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = device.Id,
        PointId = pointId,
        Operator = @operator,
        Threshold = threshold,
        ThresholdUpper = thresholdUpper,
        DurationSeconds = 0,
        Severity = severity,
        MessageTemplate = "{value} 高",
        Enabled = enabled
    };

    private sealed class TrackingAlarmRuleRepository : IAlarmRuleRepository, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetByPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetAllIncludingDisabledAsync(CancellationToken ct = default) =>
            Task.FromResult(OperationResult<IReadOnlyList<AlarmRule>>.Success(Array.Empty<AlarmRule>()));
        public Task<OperationResult> SaveAsync(AlarmRule rule, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>可编程告警规则仓储：记录 Save/Delete 调用，可注入规则列表与失败。</summary>
    private sealed class StubAlarmRuleRepository : IAlarmRuleRepository
    {
        public List<AlarmRule> Rules { get; } = [];
        public List<AlarmRule> SavedRules { get; } = [];
        public List<Guid> DeletedRules { get; } = [];

        /// <summary>GetAllIncludingDisabledAsync 是否返回失败。</summary>
        public bool FailGetAll { get; set; }

        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetAllIncludingDisabledAsync(CancellationToken ct = default) =>
            FailGetAll
                ? Task.FromResult(OperationResult<IReadOnlyList<AlarmRule>>.Failure(OperationalError.General("模拟失败")))
                : Task.FromResult(OperationResult<IReadOnlyList<AlarmRule>>.Success(Rules));

        public Task<OperationResult> SaveAsync(AlarmRule rule, CancellationToken ct = default)
        {
            SavedRules.Add(rule);
            Rules.RemoveAll(r => r.Id == rule.Id);
            Rules.Add(rule);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default)
        {
            DeletedRules.Add(ruleId);
            Rules.RemoveAll(r => r.Id == ruleId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetByPointAsync(Guid deviceId, Guid pointId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetByDeviceAsync(Guid deviceId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OperationResult<IReadOnlyList<AlarmRule>>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>可编程对话框服务：控制 EditRule/Confirm 返回值并记录调用。</summary>
    private sealed class StubAlarmRuleDialogService : IAlarmRuleDialogService
    {
        public bool EditRuleResult { get; set; } = true;
        public bool ConfirmResult { get; set; } = true;
        public Action<AlarmRuleEditor>? OnEditRule { get; set; }
        public List<AlarmRuleEditor> EditedEditors { get; } = [];
        public List<(string Title, string Message)> Confirmations { get; } = [];

        public bool EditRule(AlarmRuleEditor editor)
        {
            EditedEditors.Add(editor);
            OnEditRule?.Invoke(editor);
            return EditRuleResult;
        }

        public bool Confirm(string title, string message)
        {
            Confirmations.Add((title, message));
            return ConfirmResult;
        }
    }
}
