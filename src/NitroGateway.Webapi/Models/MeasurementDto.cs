namespace NitroGateway.Webapi.Models;
public sealed class MeasurementDto
{
    public string DeviceId { get; init; } = ""; public string DevicePointId { get; init; } = "";
    public object? RawValue { get; init; } public object? Value { get; init; }
    public string Timestamp { get; init; } = ""; public string Quality { get; init; } = ""; public string? ErrorMessage { get; init; }
}
