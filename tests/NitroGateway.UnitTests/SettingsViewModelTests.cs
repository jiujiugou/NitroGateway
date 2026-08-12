using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Desktop.Messaging;
using NitroGateway.Desktop.Services;
using NitroGateway.Desktop.ViewModels;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;
using NitroGateway.Transport.MQTT;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-033 阶段 2：设置页「从中心导入」——拉取快照 → 覆盖确认 → 重置本地；
/// 覆盖/取消/鉴权失败/空地址不触发；地址与 Token 持久化到本机设置文件。
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    /// <summary>帧间隔注入 1 小时，避免 EventBridge 后台循环干扰。</summary>
    private static readonly TimeSpan LongFrame = TimeSpan.FromHours(1);

    private readonly EventBridge _bridge;
    private readonly string _settingsFile;
    private readonly StubCenterConfigClient _client = new();
    private readonly StubCenterConfigImporter _importer = new();
    private readonly StubDeviceDialogService _dialogs = new();
    private readonly StubConfigSyncOutboxStore _outbox = new();

    public SettingsViewModelTests()
    {
        _bridge = new EventBridge(new StubForwardBuffer(), NullLogger<EventBridge>.Instance, LongFrame);
        _settingsFile = Path.Combine(Path.GetTempPath(), "nitrogateway-tests", $"{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        _bridge.Dispose();
        if (File.Exists(_settingsFile))
            File.Delete(_settingsFile);
    }

    [Fact]
    public void Constructor_loads_saved_center_settings()
    {
        var store = new CenterSyncSettingsStore(_settingsFile);
        store.Save(new CenterSyncSettings { CenterUrl = "http://center:5100", CenterToken = "tok-1" });

        var vm = CreateVm(store);

        Assert.Equal("http://center:5100", vm.CenterUrl);
        Assert.Equal("tok-1", vm.CenterToken);
    }

    [Fact]
    public async Task Import_success_confirms_then_imports_and_saves_settings()
    {
        var device = TestDevices.Device("PLC-1");
        _client.NextResult = OperationResult<IReadOnlyList<Device>>.Success(new[] { device });
        _importer.NextResult = OperationResult<ImportSummary>.Success(new ImportSummary
        {
            ImportedDevices = 1, ImportedPoints = 2, RemovedDevices = 0, RemovedPoints = 0
        });
        var vm = CreateVm(new CenterSyncSettingsStore(_settingsFile));
        vm.CenterUrl = "http://center:5100/";
        vm.CenterToken = "tok-1";

        await vm.ImportFromCenterCommand.ExecuteAsync(null);

        Assert.Equal(1, _client.Calls);
        Assert.Equal("http://center:5100/", _client.LastUrl);
        Assert.Equal("tok-1", _client.LastToken);
        Assert.Equal(1, _dialogs.ConfirmCalls);
        Assert.Equal(1, _importer.Calls);
        Assert.Same(device, Assert.Single(_importer.LastSnapshot!));
        Assert.Contains("导入完成", vm.ImportStatusText);
        // ADR-033 阶段 3/4：手动导入=以中心为准重置本地，清空待上报队列
        Assert.Equal(1, _outbox.ClearAllCalls);
        Assert.Empty(_outbox.Rows);
        // 地址/Token 已持久化到本机设置文件
        var saved = new CenterSyncSettingsStore(_settingsFile).Load();
        Assert.Equal("http://center:5100/", saved.CenterUrl);
        Assert.Equal("tok-1", saved.CenterToken);
    }

    [Fact]
    public async Task Import_cancel_does_not_touch_local_config()
    {
        _client.NextResult = OperationResult<IReadOnlyList<Device>>.Success(new[] { TestDevices.Device("PLC-1") });
        _dialogs.ConfirmResult = false;
        var vm = CreateVm(new CenterSyncSettingsStore(_settingsFile));
        vm.CenterUrl = "http://center:5100";

        await vm.ImportFromCenterCommand.ExecuteAsync(null);

        Assert.Equal(1, _client.Calls);
        Assert.Equal(0, _importer.Calls);
        Assert.Equal("已取消导入", vm.ImportStatusText);
    }

    [Fact]
    public async Task Import_auth_failure_shows_error_and_does_not_import()
    {
        _client.NextResult = OperationResult<IReadOnlyList<Device>>.Failure(OperationalError.Validation("鉴权失败（401）：Token 无效或已过期"));
        var vm = CreateVm(new CenterSyncSettingsStore(_settingsFile));
        vm.CenterUrl = "http://center:5100";
        vm.CenterToken = "bad";

        await vm.ImportFromCenterCommand.ExecuteAsync(null);

        Assert.Contains("鉴权失败", vm.ImportStatusText);
        Assert.Equal(0, _dialogs.ConfirmCalls);
        Assert.Equal(0, _importer.Calls);
    }

    [Fact]
    public void Import_without_center_url_disables_command()
    {
        // CanExecute 为 false 时按钮禁用（ExecuteAsync 直调不校验，UI 路径由 CanExecute 兜底）
        var vm = CreateVm(new CenterSyncSettingsStore(_settingsFile));
        vm.CenterToken = "tok";

        Assert.False(vm.ImportFromCenterCommand.CanExecute(null));

        vm.CenterUrl = "http://center:5100";
        Assert.True(vm.ImportFromCenterCommand.CanExecute(null));
    }

    private SettingsViewModel CreateVm(ICenterSyncSettingsStore store) => new(
        new MqttConnectionOptions { Host = "localhost", Port = 1883 },
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        _bridge,
        new UiDispatcher(),
        _client,
        _importer,
        store,
        _outbox,
        _dialogs);
}

/// <summary>ADR-033 测试替身：中心快照客户端（可编程结果 + 记录调用）。</summary>
internal sealed class StubCenterConfigClient : ICenterConfigClient
{
    public OperationResult<IReadOnlyList<Device>> NextResult { get; set; } =
        OperationResult<IReadOnlyList<Device>>.Success(Array.Empty<Device>());

    public int Calls { get; private set; }
    public string? LastUrl { get; private set; }
    public string? LastToken { get; private set; }

    public Task<OperationResult<IReadOnlyList<Device>>> FetchSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default)
    {
        Calls++;
        LastUrl = centerUrl;
        LastToken = token;
        return Task.FromResult(NextResult);
    }

    public Task<OperationResult<CenterSyncSnapshot>> FetchSyncSnapshotAsync(
        string centerUrl, string token, string siteId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<OperationResult<IReadOnlyList<CenterSyncChangeResult>>> PushChangesAsync(
        string centerUrl, string token, string siteId, IReadOnlyList<CenterSyncChange> changes,
        CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>ADR-033 测试替身：导入服务（记录快照与调用次数）。</summary>
internal sealed class StubCenterConfigImporter : ICenterConfigImporter
{
    public OperationResult<ImportSummary> NextResult { get; set; } =
        OperationResult<ImportSummary>.Success(new ImportSummary());

    public int Calls { get; private set; }
    public IReadOnlyList<Device>? LastSnapshot { get; private set; }

    public Task<OperationResult<ImportSummary>> ImportAsync(
        IReadOnlyList<Device> snapshot, CancellationToken ct = default)
    {
        Calls++;
        LastSnapshot = snapshot;
        return Task.FromResult(NextResult);
    }
}

