using NitroGateway.Host;
using Xunit;

namespace NitroGateway.UnitTests;

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