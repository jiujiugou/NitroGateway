using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NitroGateway.Alarm.Domain;
using NitroGateway.Alarm.Repository;
using NitroGateway.Desktop.Services;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 告警规则管理页（ADR-043）：为设备/点位配置「条件触发报警」。
/// 展示全部规则（含禁用），新增/编辑走模态对话框，保存后经 <see cref="IAlarmRuleRepository"/> 落库；
/// 规则仓储为 Scoped（EF DbContext），每次操作新建 scope（与 <see cref="AlarmsViewModel"/> 同模式）。
/// </summary>
public sealed partial class AlarmRulesViewModel : ObservableObject
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceSnapshotCache _cache;
    private readonly IAlarmRuleDialogService _dialogs;
    private readonly ILogger<AlarmRulesViewModel> _logger;

    public ObservableCollection<AlarmRuleItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    private AlarmRuleItem? _selectedRule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isLoading;
    [ObservableProperty] private string _statusText = "";

    /// <summary>加载完成标志（ADR-037 S3）：刷新中禁用刷新按钮，避免无反馈的防重入吞点击。</summary>
    public bool IsIdle => !IsLoading;

    private bool HasSelection => SelectedRule is not null;

    public AlarmRulesViewModel(
        IServiceScopeFactory scopeFactory,
        IDeviceSnapshotCache cache,
        IAlarmRuleDialogService dialogs,
        ILogger<AlarmRulesViewModel> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _dialogs = dialogs;
        _logger = logger;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        try
        {
            // 设备/点位名经目录缓存映射（规则只携带 Guid），缺失回退短 ID（与 AlarmsViewModel 一致）
            var devicesResult = await _cache.GetAllAsync();
            var devices = devicesResult.IsSuccess ? devicesResult.Value! : [];
            var deviceMap = devices.ToDictionary(d => d.Id);
            var pointMap = devices
                .SelectMany(d => d.Points.Select(p => (DeviceId: d.Id, Point: p)))
                .ToDictionary(x => (x.DeviceId, x.Point.Id), x => x.Point.Name);

            using var scope = _scopeFactory.CreateScope();
            var rules = scope.ServiceProvider.GetRequiredService<IAlarmRuleRepository>();
            var result = await rules.GetAllIncludingDisabledAsync();
            if (result.IsFailure)
            {
                StatusText = $"加载告警规则失败：{result.Error!.Message}";
                return;
            }

            Items.Clear();
            foreach (var rule in result.Value!)
            {
                Items.Add(AlarmRuleItem.From(
                    rule,
                    deviceMap.GetValueOrDefault(rule.DeviceId)?.Name,
                    pointMap.GetValueOrDefault((rule.DeviceId, rule.PointId))));
            }
            StatusText = $"共 {Items.Count} 条规则（含禁用）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "告警规则列表刷新失败");
            StatusText = "告警规则列表刷新失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>新增规则：模态对话框保存后 SaveAsync 落库并刷新。</summary>
    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var devicesResult = await _cache.GetAllAsync();
        if (devicesResult.IsFailure)
        {
            StatusText = $"加载设备失败：{devicesResult.Error!.Message}";
            return;
        }
        var devices = devicesResult.Value!;
        if (devices.Count == 0)
        {
            StatusText = "暂无设备，请先在「设备管理」中建设备";
            return;
        }

        var editor = new AlarmRuleEditor(devices) { Id = Guid.NewGuid() };
        if (!_dialogs.EditRule(editor))
            return;

        using var scope = _scopeFactory.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IAlarmRuleRepository>();
        var result = await rules.SaveAsync(editor.ToRule());
        if (result.IsFailure)
        {
            StatusText = $"保存告警规则失败：{result.Error!.Message}";
            return;
        }

        StatusText = "告警规则已保存";
        await RefreshAsync();
    }

    /// <summary>编辑规则：从仓储加载完整规则回填表单，保存后 upsert。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditRuleAsync()
    {
        var selected = SelectedRule;
        if (selected is null)
            return;

        var devicesResult = await _cache.GetAllAsync();
        if (devicesResult.IsFailure)
        {
            StatusText = $"加载设备失败：{devicesResult.Error!.Message}";
            return;
        }
        var devices = devicesResult.Value!;

        using var scope = _scopeFactory.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IAlarmRuleRepository>();
        var all = await rules.GetAllIncludingDisabledAsync();
        if (all.IsFailure)
        {
            StatusText = $"加载告警规则失败：{all.Error!.Message}";
            return;
        }
        var rule = all.Value!.FirstOrDefault(r => r.Id == selected.Id);
        if (rule is null)
        {
            StatusText = "所选规则已不存在";
            await RefreshAsync();
            return;
        }

        var editor = AlarmRuleEditor.FromRule(rule, devices);
        if (!_dialogs.EditRule(editor))
            return;

        var result = await rules.SaveAsync(editor.ToRule());
        if (result.IsFailure)
        {
            StatusText = $"保存告警规则失败：{result.Error!.Message}";
            return;
        }

        StatusText = "告警规则已更新";
        await RefreshAsync();
    }

    /// <summary>删除规则：确认后 DeleteAsync（会级联影响既有告警，先确认）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteRuleAsync()
    {
        var selected = SelectedRule;
        if (selected is null)
            return;

        if (!_dialogs.Confirm("删除告警规则", $"确定删除规则「{selected.DeviceName} / {selected.PointName}」？"))
            return;

        using var scope = _scopeFactory.CreateScope();
        var rules = scope.ServiceProvider.GetRequiredService<IAlarmRuleRepository>();
        var result = await rules.DeleteAsync(selected.Id);
        if (result.IsFailure)
        {
            StatusText = $"删除告警规则失败：{result.Error!.Message}";
            return;
        }

        StatusText = "告警规则已删除";
        await RefreshAsync();
    }
}

/// <summary>告警规则列表行。</summary>
public sealed partial class AlarmRuleItem : ObservableObject
{
    public Guid Id { get; init; }

    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _pointName = "";

    /// <summary>条件文案（如 "> 80" 或 "Between 80 ~ 100"）。</summary>
    [ObservableProperty] private string _condition = "";

    [ObservableProperty] private string _durationText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeverityText))]
    private AlarmSeverity _severity;

    [ObservableProperty] private bool _enabled;

    public string SeverityText => Severity.ToString();
    public string EnabledText => Enabled ? "启用" : "禁用";

    /// <summary>由领域规则 + 名称映射构造行（名称缺失回退短 ID，与 AlarmsViewModel 一致）。</summary>
    public static AlarmRuleItem From(AlarmRule rule, string? deviceName, string? pointName) => new()
    {
        Id = rule.Id,
        DeviceName = string.IsNullOrEmpty(deviceName) ? ShortId(rule.DeviceId) : deviceName,
        PointName = string.IsNullOrEmpty(pointName) ? ShortId(rule.PointId) : pointName,
        Condition = rule.Operator == "Between"
            ? $"Between {rule.Threshold:G} ~ {rule.ThresholdUpper:G}"
            : $"{rule.Operator} {rule.Threshold:G}",
        DurationText = rule.DurationSeconds <= 0 ? "立即" : $"{rule.DurationSeconds}s",
        Severity = rule.Severity,
        Enabled = rule.Enabled
    };

    private static string ShortId(Guid id) => id.ToString("N")[..8];
}
