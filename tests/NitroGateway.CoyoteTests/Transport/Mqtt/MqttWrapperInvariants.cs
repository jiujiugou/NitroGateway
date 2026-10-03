using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using NitroGateway.Transport.MQTT;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// MqttClientWrapper 并发不变量（I2/I4/I5/I6/I7）的 Coyote 检测器。
/// <para>正例对生产实现断言不变量恒成立；负控对 seeded-fault 坏实现断言调度器能找到反例。</para>
/// </summary>
internal static class MqttWrapperInvariants
{
    private const int Parallelism = 8;

    private static async Task Drain(int yields = 200)
    {
        for (var i = 0; i < yields; i++)
            await Task.Yield();
    }

    // ── I2 守卫契约：任意时刻持权 ≤1 ──

    public static Task I2_Guard_Positive()
        => RunGuardContract(new ReconnectGuard());

    /// <summary>负控：无去重坏守卫 → 并发下多流同时持权，期望被抓。</summary>
    public static Task I2_Guard_Negative_NoDedup()
        => RunGuardContract(new AlwaysAllowReconnectGuard());

    private static async Task RunGuardContract(IReconnectGuard guard)
    {
        var probe = new ConcurrencyProbe();

        var tasks = Enumerable.Range(0, Parallelism).Select(_ => Task.Run(async () =>
        {
            if (!guard.TryBegin())
                return;
            probe.Enter();
            await Task.Yield();   // 持权期间让出，使"无去重"成为可触发的交错
            probe.Exit();
            guard.End();
        })).ToArray();

        await Task.WhenAll(tasks);

        if (probe.Max > 1)
            throw new InvalidOperationException($"I2: 同时持有重连运行权应 ≤1，实际 {probe.Max}");
    }

    // ── I2 真 wrapper 级：并发触发下重连循环持权 ≤1 ──

    public static Task I2_Wrapper_Positive()
        => RunWrapperSingleFlight(new ReconnectGuard());

    /// <summary>负控：注入无去重守卫 → 多个重连循环并发，期望被抓。</summary>
    public static Task I2_Wrapper_Negative_NoDedup()
        => RunWrapperSingleFlight(new AlwaysAllowReconnectGuard());

    private static async Task RunWrapperSingleFlight(IReconnectGuard innerGuard)
    {
        var guard = new CountingGuard(innerGuard);
        var fake = new ControllableMqttInner
        {
            ConnectResultCode = MqttClientConnectResultCode.ServerUnavailable
        };

        await using var wrapper = new MqttClientWrapper(
            MqttTestData.Options(maxAttempts: 1), NullLogger<MqttClientWrapper>.Instance, fake, [], null, guard);

        var triggers = Enumerable.Range(0, Parallelism)
            .Select(_ => Task.Run(() => wrapper.ConnectAsync()))
            .ToArray();
        await Task.WhenAll(triggers);
        await Drain();

        if (guard.MaxConcurrentHolders > 1)
            throw new InvalidOperationException($"I2: 并发重连循环应 ≤1，实际 {guard.MaxConcurrentHolders}");
    }

    // ── I4 释放后不再成功触达客户端 ──

    public static async Task I4_AfterDispose_ConnectFails()
    {
        var fake = new ControllableMqttInner();
        var wrapper = new MqttClientWrapper(
            MqttTestData.Options(0), NullLogger<MqttClientWrapper>.Instance, fake, [], null, new ReconnectGuard());

        if (!(await wrapper.ConnectAsync()).IsSuccess)
            throw new InvalidOperationException("I4: 前置连接应成功");

        await wrapper.DisposeAsync();

        var after = await wrapper.ConnectAsync();
        if (!after.IsFailure)
            throw new InvalidOperationException("I4: 释放后 ConnectAsync 应失败（不得再从已释放客户端成功）");
    }

    // ── I5 开关语义：Disabled 不触达、Enable 可恢复 ──

    public static async Task I5_Disabled_DoesNotConnect_EnableRecovers()
    {
        var fake = new ControllableMqttInner();
        var toggle = new ToggleFake { IsEnabled = false };
        var wrapper = new MqttClientWrapper(
            MqttTestData.Options(0), NullLogger<MqttClientWrapper>.Instance, fake, [], toggle, new ReconnectGuard());

        var disabled = await wrapper.ConnectAsync();
        if (!disabled.IsFailure)
            throw new InvalidOperationException("I5: Disabled 时 ConnectAsync 应失败");
        if (fake.ConnectCalls != 0)
            throw new InvalidOperationException($"I5: Disabled 不应触达 inner，实际 {fake.ConnectCalls}");
        if (wrapper.State != MqttConnectionState.Disabled)
            throw new InvalidOperationException($"I5: 状态应为 Disabled，实际 {wrapper.State}");

        await toggle.SetEnabledAsync(true);
        await Drain();

        var enabled = await wrapper.ConnectAsync();
        if (!enabled.IsSuccess)
            throw new InvalidOperationException("I5: 开启后应能连接");
        if (wrapper.State != MqttConnectionState.Connected)
            throw new InvalidOperationException($"I5: 状态应为 Connected，实际 {wrapper.State}");
    }

    // ── I6 重连成功后重放订阅 ──

    public static async Task I6_Reconnect_ReplaysSubscriptions()
    {
        var fake = new ControllableMqttInner();
        var wrapper = new MqttClientWrapper(
            MqttTestData.Options(1), NullLogger<MqttClientWrapper>.Instance, fake, [], null, new ReconnectGuard());

        if (!(await wrapper.ConnectAsync()).IsSuccess)
            throw new InvalidOperationException("I6: 前置连接应成功");
        if (!(await wrapper.SubscribeAsync("nitrogateway/dev/cmd", 1)).IsSuccess)
            throw new InvalidOperationException("I6: 前置订阅应成功");

        fake.SimulateDrop();
        await Drain(300);

        var topics = fake.SubscribedTopics;
        if (topics.Count < 2)
            throw new InvalidOperationException($"I6: 重连后应重放订阅，实际订阅记录 {topics.Count}");
        if (topics[^1] != "nitrogateway/dev/cmd")
            throw new InvalidOperationException($"I6: 重放主题不符，实际 {topics[^1]}");
    }

    // ── I7 状态通知不在锁内：监听者回调重入读 State 不死锁 ──

    public static async Task I7_Listener_ReentrantState_NoDeadlock()
    {
        var fake = new ControllableMqttInner();
        var listener = new ReentrantListenerFake();
        var wrapper = new MqttClientWrapper(
            MqttTestData.Options(0), NullLogger<MqttClientWrapper>.Instance, fake, [listener], null, new ReconnectGuard());
        listener.StateProvider = () => wrapper.State;   // 回调内反向读取状态

        await wrapper.ConnectAsync();
        await wrapper.DisconnectAsync();
        await Drain();

        if (listener.Started == 0)
            throw new InvalidOperationException("I7: 监听者应被通知");
        if (listener.Completed != listener.Started)
            throw new InvalidOperationException(
                $"I7: 监听回调未完成（疑似重入死锁）started={listener.Started} completed={listener.Completed}");
    }

    // ── I3 重连 CTS 生命周期：每个创建的 CTS 恰 Dispose 一次、不泄漏 ──

    public static async Task I3_ReconnectCts_DisposedExactlyOnce()
    {
        var created = new List<ObservingCts>();
        var gate = new object();
        Func<CancellationTokenSource> factory = () =>
        {
            var cts = new ObservingCts();
            lock (gate) created.Add(cts);
            return cts;
        };

        var fake = new ControllableMqttInner
        {
            ConnectResultCode = MqttClientConnectResultCode.ServerUnavailable
        };
        var wrapper = new MqttClientWrapper(
            MqttTestData.Options(maxAttempts: 2), NullLogger<MqttClientWrapper>.Instance, fake, [], null,
            new ReconnectGuard(), factory);

        var connects = Enumerable.Range(0, 4).Select(_ => Task.Run(() => wrapper.ConnectAsync())).ToArray();
        await Task.WhenAll(connects);
        await wrapper.DisconnectAsync();
        await Drain();
        await wrapper.DisposeAsync();
        await Drain();

        lock (gate)
        {
            foreach (var cts in created)
            {
                if (cts.DisposeCount != 1)
                    throw new InvalidOperationException(
                        $"I3: 每个重连 CTS 应恰 Dispose 一次，实际 {cts.DisposeCount}");
            }
        }
    }

    // ── 探针 / seeded fault ──

    private sealed class ConcurrencyProbe
    {
        private int _holders;
        private int _max;

        public int Max => Volatile.Read(ref _max);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _holders);
            int observed;
            while (current > (observed = Volatile.Read(ref _max)))
            {
                if (Interlocked.CompareExchange(ref _max, current, observed) == observed)
                    return;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _holders);
    }

    /// <summary>seeded fault：无去重坏守卫（任何入口都放行 → 多循环并发）。</summary>
    private sealed class AlwaysAllowReconnectGuard : IReconnectGuard
    {
        public bool TryBegin() => true;
        public void End() { }
    }

    /// <summary>可观测 Dispose 次数的 CTS（用于 I3）。</summary>
    private sealed class ObservingCts : CancellationTokenSource
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }
}
