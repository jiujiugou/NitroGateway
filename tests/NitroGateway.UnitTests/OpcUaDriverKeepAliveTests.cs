using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.OpcUa;
using Opc.Ua;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-072（层3 会话自愈）AC-1/AC-4/AC-5 单测：无需真实服务器与 SDK Session。
/// 覆盖 KeepAlive 事件分类（<see cref="OpcUaDriver.ShouldStartSelfHeal"/> 纯判定）、
/// 防重入、D5 失败读在自愈窗口内不置 Faulted、以及生命周期幂等清理。
/// </summary>
public class OpcUaDriverKeepAliveTests
{
    private static OpcUaDriver CreateDriver() => new(
        new DeviceConnection { Endpoint = "opc.tcp://127.0.0.1:4840", RequestTimeoutMs = 5000 },
        NullLogger.Instance);

    private static readonly ServiceResult Bad = new(StatusCodes.BadCommunicationError);
    private static readonly ServiceResult Good = ServiceResult.Good;

    // ── AC-1：KeepAlive 事件分类（Good 无动作 / Bad 启动自愈 / 防重入）──

    [Fact]
    public void KeepAlive_GoodStatus_DoesNotStartSelfHeal() =>
        Assert.False(OpcUaDriver.ShouldStartSelfHeal(Good, S("s"), S("s"), DriverState.Connected, 0));

    [Fact]
    public void KeepAlive_NullStatus_DoesNotStartSelfHeal() =>
        Assert.False(OpcUaDriver.ShouldStartSelfHeal(null, S("s"), S("s"), DriverState.Connected, 0));

    [Fact]
    public void KeepAlive_BadWhileReconnectActive_DoesNotStartSelfHeal_AntiReentrancy()
        // D3：同一时刻只允许一个活动重连，已有重连时忽略重复 Bad
        => Assert.False(OpcUaDriver.ShouldStartSelfHeal(Bad, S("s"), S("s"), DriverState.Connected, 1));

    [Fact]
    public void KeepAlive_BadFromStaleSession_DoesNotStartSelfHeal()
        // D6：事件来自非当前会话（旧会话迟到事件）→ 忽略
        => Assert.False(OpcUaDriver.ShouldStartSelfHeal(Bad, S("stale"), S("current"), DriverState.Connected, 0));

    [Fact]
    public void KeepAlive_BadWhenNoCurrentSession_DoesNotStartSelfHeal() =>
        Assert.False(OpcUaDriver.ShouldStartSelfHeal(Bad, S("s"), null, DriverState.Connected, 0));

    [Fact]
    public void KeepAlive_BadWhenNotConnected_DoesNotStartSelfHeal()
        // D2：自愈只接管"已连接后的断线"；Disconnected/Faulted 交由 ReliableProtocolDriver 建连
        => Assert.False(OpcUaDriver.ShouldStartSelfHeal(Bad, S("s"), S("s"), DriverState.Disconnected, 0));

    [Fact]
    public void KeepAlive_BadCurrentConnectedNoActive_StartsSelfHeal()
    {
        // 同一会话引用（当前会话 KeepAlive Bad）→ 应启动自愈
        var current = S("s");
        Assert.True(OpcUaDriver.ShouldStartSelfHeal(Bad, current, current, DriverState.Connected, 0));
    }

    // ── AC-4/D5：失败读/探测在自愈窗口内不置 Faulted（保持 Connected）──

    [Fact]
    public void EnterFaulted_NoSelfHealActive_SetsFaulted()
    {
        var driver = CreateDriver();
        Assert.Equal(DriverState.Disconnected, driver.State);
        driver.EnterFaultedIfNotSelfHealing();
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public void EnterFaulted_SelfHealWindowActive_KeepsState()
    {
        var driver = CreateDriver();
        driver.SetReconnectActiveForTesting(true);
        try
        {
            driver.EnterFaultedIfNotSelfHealing();
            // 自愈窗口内不置 Faulted（D5）：状态维持 Disconnected/Connected，避免上层整轮重建抢道
            Assert.Equal(DriverState.Disconnected, driver.State);
        }
        finally
        {
            driver.SetReconnectActiveForTesting(false);
        }
    }

    [Fact]
    public void ReconnectActive_FlagRoundTrip_ReflectsState()
    {
        var driver = CreateDriver();
        Assert.False(driver.IsReconnectActiveForTesting);
        driver.SetReconnectActiveForTesting(true);
        Assert.True(driver.IsReconnectActiveForTesting);
        driver.SetReconnectActiveForTesting(false);
        Assert.False(driver.IsReconnectActiveForTesting);
    }

    // ── AC-5：生命周期清理幂等 ──

    [Fact]
    public void Dispose_Twice_NoThrow()
    {
        var driver = CreateDriver();
        driver.Dispose();
        driver.Dispose();
    }

    [Fact]
    public async Task Disconnect_WhenNeverConnected_NoThrowAndStateDisconnected()
    {
        var driver = CreateDriver();
        var r = await driver.DisconnectAsync();
        Assert.True(r.IsSuccess);
        Assert.Equal(DriverState.Disconnected, driver.State);
        await driver.DisconnectAsync();
    }

    /// <summary>测试桩引用：用于区分"同一会话"与"不同会话"的身份判定，无需真实 ISession。</summary>
    private static object S(string tag) => new TaggedRef(tag);

    private sealed record TaggedRef(string Tag);
}
