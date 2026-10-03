using ArchUnitNET.xUnit;
using Xunit;

namespace NitroGateway.ArchitectureTests;

public class ModuleCycleTests
{
    [Fact]
    public void Top_level_modules_should_be_free_of_cycles()
    {
        ModuleSlices.Given().Should().BeFreeOfCycles().Check(TestArchitecture.Architecture);
    }

    [Fact]
    public void Slice_assignment_should_cover_the_expected_modules()
    {
        var modules = ModuleSlices
            .Given()
            .GetObjects(TestArchitecture.Architecture)
            .Select(slice => slice.Description)
            .ToHashSet();

        var expected = new[]
        {
            "Domain",
            "Protocols",
            "Persistence",
            "Collection",
            "Command",
            "Forwarder",
            "Host",
            "Desktop",
            "Webapi",
        };
        var missing = expected.Where(module => !modules.Contains(module)).ToList();

        Assert.True(
            missing.Count == 0 && modules.Count >= 16,
            $"modules({modules.Count}): {string.Join(", ", modules)} | missing: {string.Join(", ", missing)}"
        );
    }
}
