using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.OpcUa;
using Opc.Ua;

namespace NitroGateway.CoyoteTests;

/// <summary>
/// OpcUaDriver 会话自愈并发不变量（对应 ADR-072 D2/D3/D5，ADR-077/078）。
/// <para><b>可测缝：</b>真实 <c>Session</c> 非线程安全且需网络，无法在 Coyote 内构造；
/// 本文件经 <see cref="OpcUaDriver.HandleKeepAliveBad"/> + 身份/状态/启动替身
/// （<c>CurrentSessionOverrideForTesting</c> / <c>StateOverrideForTesting</c> /
/// <c>ReconnectStarterOverrideForTesting</c>）驱动与生产完全相同的"快速判定 → 取闸门 → 复核 →
/// 原子抢占 → 闸门内启动"路径，不触达 SDK。</para>
/// <para><b>覆盖边界：</b>非目标：真实会话读写/Browse/订阅的闸门串行（需 SDK，见 IntegrationTests）、
/// 重连完成回调（<c>OnReconnectComplete</c>）与订阅迁移（需真实 <c>SessionReconnectHandler</c>）。</para>
/// </summary>
internal static class OpcUaDriverInvariants
{
    private const int Parallelism = 8;

    private static readonly ServiceResult Bad = new(StatusCodes.BadCommunicationError);

    /// <summary>构造已连接、带替身会话的驱动（不触达 SDK）。</summary>
    private static OpcUaDriver NewConnectedDriver(object session)
    {
        var driver = new OpcUaDriver(
            new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840", RequestTimeoutMs = 5000 },
            NullLogger.Instance);
        driver.CurrentSessionOverrideForTesting = session;
        driver.StateOverrideForTesting = DriverState.Connected;
        driver.ReconnectStarterOverrideForTesting = _ => { };
        return driver;
    }

    // ── I1/D3：并发 Bad 只允许一次启动自愈（防重入位单胜者）──

    public static Task I1_AntiReentry_SingleWinner_Positive()
    {
        var session = new object();
        var driver = NewConnectedDriver(session);
        return RunSingleWinner(() => Task.FromResult(driver.HandleKeepAliveBad(session, Bad)), driver);
    }

    /// <summary>负控：无闸门/非原子的 check-then-act 坏闩锁，并发下多路胜出 → 期望被抓。</summary>
    public static Task I1_AntiReentry_Negative_CheckThenAct()
    {
        var latch = new RacySelfHealLatch();
        return RunSingleWinner(latch.TryStartAsync, probe: null);
    }

    private static async Task RunSingleWinner(Func<Task<bool>> tryStart, OpcUaDriver? probe)
    {
        var starts = 0;
        var tasks = Enumerable.Range(0, Parallelism)
            .Select(_ => Task.Run(async () =>
            {
                if (await tryStart())
                    Interlocked.Increment(ref starts);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var count = Volatile.Read(ref starts);
        if (count != 1)
            throw new InvalidOperationException($"I1(UA): 并发 Bad 应恰启动一次自愈，实际 {count}");

        // 抢占后防重入位应保持置位（D3 粘滞）
        if (probe is not null && !probe.IsReconnectActiveForTesting)
            throw new InvalidOperationException("I1(UA): 抢占成功后防重入位应保持置位");
    }

    // ── I2/D2/D6：非当前会话 / Good 状态不参与抢占（并发混杂下仍仅真实当前会话可胜）──

    public static Task I2_Classification_GatesConcurrentBad_Positive()
    {
        var current = new object();
        var stale = new object();
        var driver = NewConnectedDriver(current);

        // 混杂：当前会话 Bad（可胜） + 旧会话 Bad（应拒） + Good（应拒）
        var attempts = new List<Func<Task<bool>>>();
        for (var i = 0; i < Parallelism; i++)
        {
            attempts.Add(() => Task.FromResult(driver.HandleKeepAliveBad(current, Bad)));
            attempts.Add(() => Task.FromResult(driver.HandleKeepAliveBad(stale, Bad)));
            attempts.Add(() => Task.FromResult(driver.HandleKeepAliveBad(current, ServiceResult.Good)));
        }
        return RunSingleWinnerRoundRobin(attempts, probe: driver);
    }

    /// <summary>负控：忽略会话身份/状态分类、对任意 Bad 都启动的坏实现 → 期望多路胜出被抓。</summary>
    public static Task I2_Classification_Negative_IgnoresIdentity()
    {
        var attempts = Enumerable.Range(0, Parallelism)
            .Select(_ => (Func<Task<bool>>)(() => Task.FromResult(true)))   // 坏：无身份校验、无防重入
            .ToList();
        return RunSingleWinnerRoundRobin(attempts, probe: null);
    }

    private static async Task RunSingleWinnerRoundRobin(
        IReadOnlyList<Func<Task<bool>>> attempts, OpcUaDriver? probe)
    {
        var starts = 0;
        var tasks = Enumerable.Range(0, Parallelism)
            .Select(i => Task.Run(async () =>
            {
                if (await attempts[i % attempts.Count]())
                    Interlocked.Increment(ref starts);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var count = Volatile.Read(ref starts);
        if (count != 1)
            throw new InvalidOperationException($"I2(UA): 混杂 Bad 应恰启动一次自愈（仅当前会话），实际 {count}");

        if (probe is not null && !probe.IsReconnectActiveForTesting)
            throw new InvalidOperationException("I2(UA): 抢占成功后防重入位应保持置位");
    }

    // ── I3/D5：自愈窗口内失败读不置 Faulted（保持状态，避免与上层整轮重建抢道）──

    public static async Task I3_FaultSuppression_InWindow_Positive()
    {
        var session = new object();
        var driver = NewConnectedDriver(session);
        if (!driver.HandleKeepAliveBad(session, Bad))
            throw new InvalidOperationException("I3(UA): 前置：自愈窗口应成功打开");

        await RunFaultSuppression(driver.EnterFaultedIfNotSelfHealing, () => driver.State);
    }

    /// <summary>负控：忽略自愈窗口、失败读无条件置 Faulted 的坏实现 → 期望被抓。</summary>
    public static Task I3_FaultSuppression_Negative_AlwaysFaulted()
    {
        var suppressor = new IgnoreWindowSuppressor();
        suppressor.OpenWindow();
        return RunFaultSuppression(suppressor.EnterFaulted, () => suppressor.State);
    }

    private static async Task RunFaultSuppression(Action enterFaulted, Func<DriverState> state)
    {
        var tasks = Enumerable.Range(0, Parallelism).Select(_ => Task.Run(enterFaulted)).ToArray();
        await Task.WhenAll(tasks);

        if (state() == DriverState.Faulted)
            throw new InvalidOperationException("I3(UA): 自愈窗口内失败读不应置 Faulted");
    }

    // ══════════════ 坏实现（negative-control 种子） ══════════════

    /// <summary>无闸门、非原子 check-then-act 的自愈闩锁：并发下多路胜出。</summary>
    private sealed class RacySelfHealLatch
    {
        private int _active;

        public async Task<bool> TryStartAsync()
        {
            if (Volatile.Read(ref _active) != 0)
                return false;
            await Task.Yield();               // 坏：check 与 act 之间存在交错窗口
            if (Volatile.Read(ref _active) != 0)
                return false;
            Interlocked.Exchange(ref _active, 1);
            return true;
        }
    }

    /// <summary>忽略自愈窗口、失败读无条件置 Faulted 的坏抑制器。</summary>
    private sealed class IgnoreWindowSuppressor
    {
        private DriverState _state = DriverState.Connected;

        public DriverState State => _state;

        public void OpenWindow() { }

        public void EnterFaulted() => _state = DriverState.Faulted;
    }
}
