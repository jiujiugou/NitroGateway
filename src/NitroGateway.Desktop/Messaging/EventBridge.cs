using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.Messaging;

/// <summary>
/// 一帧 UI 数据（ADR-026 D2）。由 <see cref="EventBridge"/> 每 200ms 合并一次，
/// 携带本帧内的点位快照、设备健康变更、MQTT 状态与缓冲水位（水位每 2s 刷新一次）。
/// </summary>
public sealed record UiFrame
{
    /// <summary>本帧内的点位快照（按事件到达顺序）</summary>
    public IReadOnlyList<PointSnapshot> Measurements { get; init; } = [];

    /// <summary>本帧内的设备健康变更</summary>
    public IReadOnlyList<DeviceHealthChanged> HealthChanges { get; init; } = [];

    /// <summary>
    /// MQTT 最近已知状态（设置后每帧携带；消费方按文本幂等覆盖即可，
    /// 无变更检测需求——ADR-027 P3-1 注释对齐实现）
    /// </summary>
    public MqttConnectionState? MqttState { get; init; }

    /// <summary>转发缓冲积压批数（本轮有刷新且变化时为值，否则 null）</summary>
    public int? BufferBacklog { get; init; }

    /// <summary>帧是否为空（无任何数据，跳过发布）</summary>
    public bool IsEmpty =>
        Measurements.Count == 0 && HealthChanges.Count == 0 &&
        MqttState is null && BufferBacklog is null;
}

/// <summary>
/// 服务事件 → UI 事件桥（ADR-026 D2）。接收采集/健康/MQTT 三类事件，
/// 每 200ms 合并成一帧 <see cref="UiFrame"/> 发布；缓冲水位每 2s 轮询一次（10 帧）。
/// <para><b>设计意图：</b>UI 只消费帧，避免每点切 Dispatcher 卡 UI；批量合并刷新由
/// ViewModel 侧字典完成（单点数据直刷、批量合并刷新）。</para>
/// </summary>
public sealed class EventBridge : IDisposable, IPointStoredSink, IDeviceHealthListener, IMqttStateListener
{
    /// <summary>默认帧间隔：200ms（ADR-026 D2）。</summary>
    public static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>缓冲水位轮询频率：每 10 帧一次（200ms × 10 = 2s）。</summary>
    private const int BacklogPollFrames = 10;

    private readonly object _gate = new();
    private readonly List<PointSnapshot> _pendingMeasurements = [];
    private readonly List<DeviceHealthChanged> _pendingHealth = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly IForwardBuffer _buffer;
    private readonly ILogger<EventBridge> _logger;
    private readonly TimeSpan _frameInterval;
    /// <summary>帧循环异常后的重启延迟（ADR-028 P3-1 自愈）</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(200);

    private MqttConnectionState? _mqttState;
    private int? _backlog;
    private bool _backlogDirty;
    private int _tick;
    private int _disposed;

    /// <summary>帧就绪事件（后台线程触发；ViewModel 侧经 UiDispatcher 贴回 UI 线程）。</summary>
    public event Action<UiFrame>? FrameReady;

    /// <summary>
    /// 创建桥。构造函数即启动 200ms 帧循环。
    /// </summary>
    /// <param name="buffer">转发缓冲，用于轮询积压水位</param>
    /// <param name="logger">日志</param>
    /// <param name="frameInterval">帧间隔；测试可注入更小值</param>
    public EventBridge(IForwardBuffer buffer, ILogger<EventBridge> logger, TimeSpan? frameInterval = null)
    {
        _buffer = buffer;
        _logger = logger;
        _frameInterval = frameInterval ?? DefaultFrameInterval;
        _loop = Task.Run(LoopAsync);
    }

    /// <inheritdoc />
    public ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
    {
        lock (_gate) _pendingMeasurements.AddRange(e.Snapshots);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        lock (_gate) _pendingHealth.Add(e);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnStateChangedAsync(MqttConnectionState state, CancellationToken ct = default)
    {
        lock (_gate) _mqttState = state;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 帧循环：每 200ms 刷新水位（每 10 帧）并发布一帧。
    /// ADR-028 P3-1：循环异常后不再直接退出（修复前 UI 数据永久静止）——记 Error 后重建 PeriodicTimer
    /// 重启循环；重启延迟 200ms，非连续异常下用户无感知，连续异常时至少保留日志与重试。
    /// </summary>
    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            using var timer = new PeriodicTimer(_frameInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    _tick++;
                    if (_tick % BacklogPollFrames == 0)
                        await RefreshBacklogAsync();
                    Flush();
                }
            }
            catch (OperationCanceledException)
            {
                return; // 正常释放
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventBridge 帧循环异常，{Delay}ms 后重启循环", RestartDelay.TotalMilliseconds);
                try { await Task.Delay(RestartDelay, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>轮询转发缓冲水位；变化时标记 dirty 供下一帧携带。</summary>
    internal async Task RefreshBacklogAsync()
    {
        try
        {
            var count = await _buffer.GetCountAsync(_cts.Token);
            lock (_gate)
            {
                if (count != _backlog)
                {
                    _backlog = count;
                    _backlogDirty = true;
                }
            }
        }
        catch (OperationCanceledException) { /* 释放中 */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "转发缓冲水位查询失败");
        }
    }

    /// <summary>
    /// 取走当前累积数据并发布一帧。空帧不发布。
    /// 测试可手动调用（<see cref="LoopAsync"/> 之外）。
    /// </summary>
    internal void Flush()
    {
        UiFrame frame;
        lock (_gate)
        {
            frame = new UiFrame
            {
                Measurements = _pendingMeasurements.ToArray(),
                HealthChanges = _pendingHealth.ToArray(),
                MqttState = _mqttState,
                BufferBacklog = _backlogDirty ? _backlog : null
            };
            _pendingMeasurements.Clear();
            _pendingHealth.Clear();
            _backlogDirty = false;
        }

        if (frame.IsEmpty)
            return;

        try { FrameReady?.Invoke(frame); }
        catch (Exception ex) { _logger.LogError(ex, "EventBridge 帧分发异常"); }
    }

    public void Dispose()
    {
        // 幂等：同一单例经工厂注册可能被容器跟踪两次，二次 Dispose 直接返回
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        try { _loop.GetAwaiter().GetResult(); } catch { /* 循环异常已隔离 */ }
        _cts.Dispose();
    }
}

