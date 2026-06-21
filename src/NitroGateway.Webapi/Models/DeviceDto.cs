namespace NitroGateway.Webapi.Models;

public sealed class DeviceDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ProtocolDto Protocol { get; init; } = new();
    public ConnectionDto Connection { get; init; } = new();
    public string Status { get; init; } = "";
    public List<PointDto> Points { get; init; } = [];
}

public sealed class ProtocolDto { public string Name { get; init; } = ""; public string? Dialect { get; init; } }

public sealed class ConnectionDto
{
    public string Endpoint { get; init; } = "";
    public int ConnectTimeoutMs { get; init; }
    public int RequestTimeoutMs { get; init; }
    public int RetryCount { get; init; }
    public int RetryIntervalMs { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = [];
}

public sealed class PointDto
{
    public string Id { get; init; } = ""; public string Name { get; init; } = ""; public string Address { get; init; } = "";
    public string? Description { get; init; } public string DataType { get; init; } = ""; public string Access { get; init; } = "";
    public bool Enabled { get; init; } public int ScanIntervalMs { get; init; }
    public double Deadband { get; init; } public double ScaleFactor { get; init; } public double ScaleOffset { get; init; }
}

public sealed class DeviceStatusSummaryDto { public string DeviceId { get; init; } = ""; public string DeviceName { get; init; } = ""; public string Status { get; init; } = ""; public string? LastError { get; init; } }
