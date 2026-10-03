using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Slices;

namespace NitroGateway.ArchitectureTests;

internal static class ModuleSlices
{
    private const string RootPrefix = "NitroGateway.";

    private static readonly SliceAssignment Assignment = new(
        SliceIdentifierFor,
        "NitroGateway.<Module> 顶层模块"
    );

    public static GivenSlices Given()
    {
        var creator = new SliceRuleCreator();
        creator.SetSliceAssignment(Assignment);
        return new GivenSlices(creator);
    }

    private static SliceIdentifier SliceIdentifierFor(IType type)
    {
        var ns = type.Namespace.FullName;
        if (string.IsNullOrEmpty(ns) || !ns.StartsWith(RootPrefix, StringComparison.Ordinal))
        {
            return SliceIdentifier.Ignore();
        }

        var module = ns.Substring(RootPrefix.Length).Split('.')[0];
        return module.Length == 0 ? SliceIdentifier.Ignore() : SliceIdentifier.Of(module);
    }
}
