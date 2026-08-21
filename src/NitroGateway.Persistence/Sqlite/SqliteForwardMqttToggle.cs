using Microsoft.Extensions.Logging;
using NitroGateway.Shared;
using NitroGateway.Storage.Buffer;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>
/// MQTT 上云转发总开关的 Webapi 宿主实现（ADR-059）：持久化到 app_meta 键值表
/// （key='forwarder_mqtt_enabled'，value='true'|'false'），重启保持；缺省视为启用。
/// <see cref="IsEnabled"/> 为内存缓存（Volatile 读写），供采集热路径同步读取，不落库。
/// </summary>
public sealed class SqliteForwardMqttToggle : IForwardMqttToggle
{
    /// <summary>app_meta 键名（ADR-059）：运行期 MQTT 上云转发开关</summary>
    public const string Key = "forwarder_mqtt_enabled";

    private const int EnabledTrue = 1;
    private const int EnabledFalse = 0;

    private readonly IAppMetaStore _store;
    private readonly ILogger<SqliteForwardMqttToggle> _logger;
    private int _enabled = EnabledTrue; // 缺省启用

    /// <inheritdoc />
    public event Action<bool>? EnabledChanged;

    /// <param name="store">app_meta 键值存储</param>
    /// <param name="logger">日志记录器</param>
    public SqliteForwardMqttToggle(IAppMetaStore store, ILogger<SqliteForwardMqttToggle> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => Volatile.Read(ref _enabled) == EnabledTrue;

    /// <inheritdoc />
    /// <remarks>读取失败不阻断启动：按默认启用处理（与"缺省启用"语义一致），仅记 Warning。</remarks>
    public async Task<OperationResult> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var value = await _store.GetAsync(Key, ct);
            var enabled = value is null || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            Volatile.Write(ref _enabled, enabled ? EnabledTrue : EnabledFalse);
            _logger.LogInformation("MQTT 转发开关初始化: {Enabled}（app_meta key={Key}）", enabled, Key);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _enabled, EnabledTrue);
            var error = SqliteErrorClassifier.Classify(ex, "MQTT 转发开关初始化失败");
            _logger.LogWarning("{Context}，按启用处理: {Error}", "MQTT 转发开关初始化失败", error.Message);
            return OperationResult.Success();
        }
    }

    /// <inheritdoc />
    /// <remarks>持久化成功后才更新内存态；失败返回失败结果且内存态不变（Controller 据此 400）。</remarks>
    public async Task<OperationResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        try
        {
            await _store.SetAsync(Key, enabled ? "true" : "false", ct);
            // ADR-061：仅在实际值变化时触发事件，避免 UI 重复点同一值造成多余断开/重连
            var changed = Volatile.Read(ref _enabled) != (enabled ? EnabledTrue : EnabledFalse);
            Volatile.Write(ref _enabled, enabled ? EnabledTrue : EnabledFalse);
            _logger.LogInformation("MQTT 转发开关已切换: {Enabled}", enabled);
            if (changed)
                EnabledChanged?.Invoke(enabled);
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = SqliteErrorClassifier.Classify(ex, "MQTT 转发开关写入失败");
            _logger.LogWarning("{Context}: {Error}", "MQTT 转发开关写入失败", error.Message);
            return OperationResult.Failure(error);
        }
    }
}
