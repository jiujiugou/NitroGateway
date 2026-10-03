using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace NitroGateway.ArchitectureTests;

public class LayeringTests
{
    [Fact]
    public void Domain_should_not_depend_on_infrastructure()
    {
        var infrastructure = Types().That()
            .ResideInNamespaceMatching("^NitroGateway\\.Persistence")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Webapi")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Desktop")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Collection")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Forwarder")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.DeviceManagement")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Alarm")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Command")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Security")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Protocols")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Transport")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Storage")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Telemetry")
            .Or().ResideInNamespaceMatching("^NitroGateway\\.Host");

        Types().That().ResideInNamespaceMatching("^NitroGateway\\.Domain")
            .Should().NotDependOnAny(infrastructure)
            .Check(TestArchitecture.Architecture);
    }
}
