using NitroGateway.Host;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-016 P1-1：GatewayLifecycle 语义——draining/stopped 单向推进，
/// RequestStop 不再把标志复位为 false（原实现语义与命名相反）。
/// </summary>
public class GatewayLifecycleTests
{
    [Fact]
    public void Initial_NeitherDrainingNorStopped()
    {
        var lc = new GatewayLifecycle();
        Assert.False(lc.IsDraining);
        Assert.False(lc.IsStopped);
    }

    [Fact]
    public void RequestStop_MarksDraining()
    {
        var lc = new GatewayLifecycle();
        lc.RequestStop();
        Assert.True(lc.IsDraining);
        Assert.False(lc.IsStopped);
    }

    [Fact]
    public void MarkStopped_MarksStopped_KeepsDraining()
    {
        var lc = new GatewayLifecycle();
        lc.RequestStop();
        lc.MarkStopped();
        Assert.True(lc.IsDraining);
        Assert.True(lc.IsStopped);
    }
}