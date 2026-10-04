using Microsoft.Extensions.Logging;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Events;
using NitroGateway.Storage.Buffer;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.Desktop.Messaging;

/// <summary>
/// 服务事件 → UI 事件桥（ADR-026 D2）。接收采集/健康/MQTT 三类事件，
/// 每 200ms 合并成一帧 <see cref="UiFrame"/> 发布；缓冲水位每 2s 轮询一次（10 帧）。
/// <para><b>设计意图：</b>UI 只消费帧，避免每点切 Dispatcher 卡 UI；批量合并刷新由
/// ViewModel 侧字典完成（单点数据直刷、批量合并刷新）。</para>
/// <para><b>线程模型：</b>三个 <c>OnXxxAsync</c> 由后台线程并发调用，仅写入受
/// <see cref="_gate"/> 保护的缓冲；<see cref="LoopAsync"/> 在后台任务中定时取走并触发
/// <see cref="FrameReady"/>，因此订阅方必须自行经 UiDispatcher 贴回 UI 线程。</para>
/// <para><b>健壮性：</b>帧循环异常被隔离并在 200ms 后重启；<see cref="Dispose"/> 幂等，
/// 可安全应对容器对同一单例的重复释放。</para>
/// </summary>
public sealed class EventBridge : IDisposable, IPointStoredSink, IDeviceHealthListener, IMqttStateListener
{
    /// <summary>默认帧间隔：200ms（ADR-026 D2）。</summary>
    public static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>缓冲水位轮询频率：每 10 帧一次（200ms × 10 = 2s）。</summary>
    private const int BacklogPollFrames = 10;

    /// <summary>缓冲与状态字段的互斥锁：三个事件回调（后台线程）与帧循环（后台任务）共用。</summary>
    private readonly object _gate = new();

    /// <summary>本帧待合并的点位快照；<see cref="Flush"/> 后清空。</summary>
    private readonly List<PointSnapshot> _pendingMeasurements = [];

    /// <summary>本帧待合并的设备健康变更；<see cref="Flush"/> 后清空。</summary>
    private readonly List<DeviceHealthChanged> _pendingHealth = [];

    /// <summary>帧循环取消源；<see cref="Dispose"/> 中取消以停止后台循环。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>后台帧循环任务；<see cref="Dispose"/> 中同步等待其结束。</summary>
    private readonly Task _loop;

    /// <summary>转发缓冲，用于轮询积压水位（<see cref="RefreshBacklogAsync"/>）。</summary>
    private readonly IForwardBuffer _buffer;

    /// <summary>日志器。</summary>
    private readonly ILogger<EventBridge> _logger;

    /// <summary>实际帧间隔（构造时可注入更小值供测试）。</summary>
    private readonly TimeSpan _frameInterval;

    /// <summary>帧循环异常后的重启延迟。</summary>
    private static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>最新 MQTT 状态；仅保存最后一个值，每次触发 <see cref="UiFrame.MqttState"/> 都带上。</summary>
    private MqttConnectionState? _mqttState;

    /// <summary>最近一次轮询到的转发缓冲积压批数。</summary>
    private int? _backlog;

    /// <summary>水位自上次 <see cref="Flush"/> 后是否发生变化；仅变化时才随帧携带。</summary>
    private bool _backlogDirty;

    /// <summary>帧计数；用于按 <see cref="BacklogPollFrames"/> 节流水位轮询。</summary>
    private int _tick;

    /// <summary>释放标志（0/1）：<see cref="Dispose"/> 以 <see cref="Interlocked.Exchange"/> 保证只执行一次。</summary>
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
        // 立即在后台任务启动帧循环；不阻塞构造方（DI 解析发生在 UI 线程）。
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// 采集数据落库回调（<see cref="IPointStoredSink"/> 实现）：将本批点位快照追加到待合并缓冲。
    /// 由后台线程调用，仅做加锁入队，不做任何 UI 操作。
    /// </summary>
    /// <param name="e">落库事件，携带本批点位快照。</param>
    /// <param name="ct">取消令牌（此处未使用，入队为即时操作）。</param>
    /// <returns>已完成的任务。</returns>
    public ValueTask OnStoredAsync(PointStoredEvent e, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _pendingMeasurements.AddRange(e.Snapshots);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 设备健康变更回调（<see cref="IDeviceHealthListener"/> 实现）：追加到待合并缓冲。
    /// 由后台线程调用，仅做加锁入队。
    /// </summary>
    /// <param name="e">健康变更事件。</param>
    /// <param name="ct">取消令牌（此处未使用）。</param>
    /// <returns>已完成的任务。</returns>
    public ValueTask OnHealthChangedAsync(DeviceHealthChanged e, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _pendingHealth.Add(e);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// MQTT 状态变更回调（<see cref="IMqttStateListener"/> 实现）：仅保存最新状态，
    /// 后续每帧都会携带，直到被新状态覆盖。
    /// </summary>
    /// <param name="state">最新 MQTT 连接状态。</param>
    /// <param name="ct">取消令牌（此处未使用）。</param>
    /// <returns>已完成的任务。</returns>
    public ValueTask OnStateChangedAsync(MqttConnectionState state, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _mqttState = state;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 后台帧循环：每 <see cref="_frameInterval"/> 触发一次，按 <see cref="BacklogPollFrames"/>
    /// 节流刷新水位，然后 <see cref="Flush"/> 发布一帧。异常被隔离后延迟重启，避免单次异常
    /// 永久中断 UI 数据通道；取消时正常退出。
    /// </summary>
    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // 每轮重建 timer：异常重启后计时重新开始（与 PeriodicTimer 语义一致）。
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

    /// <summary>
    /// 轮询转发缓冲水位；变化时标记 dirty 供下一帧携带。
    /// 查询失败仅记警告，不影响帧循环继续运行。
    /// </summary>
    internal async Task RefreshBacklogAsync()
    {
        try
        {
            var count = await _buffer.GetCountAsync(_cts.Token);
            lock (_gate)
            {
                // 仅当积压数变化时标记 dirty，避免每帧重复下发相同水位。
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
    /// <para>加锁内只做数据快照与清空，事件分发放在锁外，避免订阅方回调造成锁重入或长阻塞。</para>
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

        // 单个订阅方异常不得影响其他订阅方与帧循环。
        try { FrameReady?.Invoke(frame); }
        catch (Exception ex) { _logger.LogError(ex, "EventBridge 帧分发异常"); }
    }

    /// <summary>
    /// 停止帧循环并释放取消源。幂等：以 <see cref="_disposed"/> 的原子交换保证只执行一次
    /// （同一单例经工厂注册可能被容器跟踪两次）。同步等待循环结束，循环内异常已被隔离。
    /// </summary>
    public void Dispose()
    {
        // 幂等：同一单例经工厂注册可能被容器跟踪两次，二次 Dispose 直接返回
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        // 有限等待：循环正常时秒退；万一卡在 DB/IO 上也只等 2s，绝不无限阻塞宿主释放。
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 循环异常/超时已隔离，交由进程退出回收 */ }
        _cts.Dispose();
    }
}
