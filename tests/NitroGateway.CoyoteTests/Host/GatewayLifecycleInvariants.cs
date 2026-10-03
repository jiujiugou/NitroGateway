using NitroGateway.Host;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// GatewayLifecycle 不变量（对应 notes/Invariants/gateway-lifecycle.md I1）：
/// I1 单调不可回退——并发 RequestStop/MarkStopped 后两标志终态皆 true。
/// <para>正例跑真实 <see cref="GatewayLifecycle"/>；负控跑会翻转的坏状态 → 断言变红。</para>
/// </summary>
internal static class GatewayLifecycleInvariants
{
    public static Task I1_Monotonic_Positive() => RunMonotonic(new RealLifecycle(new GatewayLifecycle()));

    /// <summary>负控：每次调用翻转标志 → 偶数次后不为 true。</summary>
    public static Task I1_Monotonic_Negative_Toggling() => RunMonotonic(new BrokenLifecycle());

    private static async Task RunMonotonic(ILifecycle lifecycle)
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0) lifecycle.RequestStop();
            else lifecycle.MarkStopped();
        })));

        if (!lifecycle.IsDraining || !lifecycle.IsStopped)
            throw new InvalidOperationException(
                $"I1: 并发停止后标志应皆为 true，实际 IsDraining={lifecycle.IsDraining} IsStopped={lifecycle.IsStopped}");
    }

    private interface ILifecycle
    {
        bool IsDraining { get; }
        bool IsStopped { get; }
        void RequestStop();
        void MarkStopped();
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>把真实 <see cref="GatewayLifecycle"/> 适配到 ILifecycle。</summary>
    private sealed class RealLifecycle(GatewayLifecycle inner) : ILifecycle
    {
        public bool IsDraining => inner.IsDraining;
        public bool IsStopped => inner.IsStopped;
        public void RequestStop() => inner.RequestStop();
        public void MarkStopped() => inner.MarkStopped();
    }

    /// <summary>坏实现：标志被翻转而非置 true → 并发偶数次调用后回到 false。</summary>
    private sealed class BrokenLifecycle : ILifecycle
    {
        private bool _draining;
        private bool _stopped;

        public bool IsDraining => _draining;
        public bool IsStopped => _stopped;
        public void RequestStop() => _draining = !_draining;
        public void MarkStopped() => _stopped = !_stopped;
    }
}
