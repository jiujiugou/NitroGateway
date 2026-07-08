namespace NitroGateway.Webapi.Models;

/// <summary>设备健康 DTO</summary>
public class DeviceHealthDto
{
    public string DeviceId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? LastCollectionAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public string? LastError { get; set; }
}
