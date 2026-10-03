using ArchUnitNET.Domain;
using ArchUnitNET.Loader;

namespace NitroGateway.ArchitectureTests;

internal static class TestArchitecture
{
    private static readonly string[] AssemblyNames =
    [
        "NitroGateway.Alarm",
        "NitroGateway.Command",
        "NitroGateway.Collection",
        "NitroGateway.Desktop",
        "NitroGateway.Device",
        "NitroGateway.Domain",
        "NitroGateway.Forwarder",
        "NitroGateway.Host",
        "NitroGateway.Persistence",
        "NitroGateway.Primitives",
        "NitroGateway.Protocol.Abstractions",
        "NitroGateway.Protocol.Mitsubishi",
        "NitroGateway.Protocol.Modbus",
        "NitroGateway.Protocol.OpcUa",
        "NitroGateway.Protocol.S7",
        "NitroGateway.Protocols",
        "NitroGateway.Security",
        "NitroGateway.Shared",
        "NitroGateway.Storage.Buffer",
        "NitroGateway.Storage.Configuration",
        "NitroGateway.Storage.Disk",
        "NitroGateway.Storage.TimeSeries",
        "NitroGateway.Telemetry",
        "NitroGateway.Transport.HTTP",
        "NitroGateway.Transport.MQTT",
        "NitroGateway.Webapi"
    ];

    public static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(AssemblyNames.Select(name => System.Reflection.Assembly.Load(name)).ToArray())
        .Build();
}
