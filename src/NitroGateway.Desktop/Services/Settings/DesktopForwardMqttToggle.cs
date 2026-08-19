using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Desktop.Services.Settings;

/// <summary>
/// MQTT 上云转发总开关的桌面端实现（ADR-059）：持久化到 desktop-settings.json
/// （<see cref="DesktopSettings.ForwarderMqttEnabled"/>），重启保持；缺省视为启用。
/// <see cref="IsEnabled"/> 为内存缓存（Volatile 读写），供采集热路径同步读取，不落盘。
/// </summary>
public sealed class DesktopForwardMqttToggle : IForwardMqttToggle
{
    private const int EnabledTrue = 1;
    private const int EnabledFalse = 0;

    private readonly IDesktopSettingsStore _store;
    private readonly ILogger<DesktopForwardMqttToggle> _logger;
    private int _enabled = EnabledTrue; // 缺省启用

    /// <param name="store">桌面本地设置存储（desktop-settings.json）</param>
    /// <param name="logger">日志记录器</param>
    public DesktopForwardMqttToggle(IDesktopSettingsStore store, ILogger<DesktopForwardMqttToggle> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => Volatile.Read(ref _enabled) == EnabledTrue;

    /// <inheritdoc />
    /// <remarks>读取失败不阻断启动：按默认启用处理（与"缺省启用"语义一致），仅记 Warning。</remarks>
    public Task<OperationResult> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var enabled = _store.Load().ForwarderMqttEnabled;
            Volatile.Write(ref _enabled, enabled ? EnabledTrue : EnabledFalse);
            _logger.LogInformation("MQTT 转发开关初始化: {Enabled}", enabled);
            return Task.FromResult(OperationResult.Success());
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _enabled, EnabledTrue);
            _logger.LogWarning("MQTT 转发开关初始化失败，按启用处理: {Error}", ex.Message);
            return Task.FromResult(OperationResult.Success());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 采用「加载→改字段→保存」合并写：只改 ForwarderMqttEnabled，保留 LogDirectory，
    /// 避免与设置页"保存日志目录"互相覆盖（反之亦然）。
    /// 持久化成功后才更新内存态；失败返回失败结果且内存态不变。
    /// </remarks>
    public async Task<OperationResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        try
        {
            var settings = _store.Load();
            settings.ForwarderMqttEnabled = enabled;
            _store.Save(settings);
            Volatile.Write(ref _enabled, enabled ? EnabledTrue : EnabledFalse);
            _logger.LogInformation("MQTT 转发开关已切换: {Enabled}", enabled);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            var error = OperationalError.Storage($"MQTT 转发开关写入失败: {ex.Message}");
            _logger.LogWarning("{Error}", error.Message);
            return OperationResult.Failure(error);
        }
    }
}
