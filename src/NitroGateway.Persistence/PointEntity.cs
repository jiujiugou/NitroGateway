namespace NitroGateway.Persistence;

/// <summary>点位 EF 实体</summary>
public sealed class PointEntity
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public required string Name { get; set; }
    public required string Address { get; set; }
    public string? Description { get; set; }

    /// <summary>DataType 字符串</summary>
    public required string DataType { get; set; }

    /// <summary>PointAccess 字符串</summary>
    public required string Access { get; set; }

    public bool Enabled { get; set; } = true;
    public int ScanIntervalMs { get; set; }
    public double Deadband { get; set; }
    public double ScaleFactor { get; set; } = 1.0;
    public double ScaleOffset { get; set; }

    public DeviceEntity Device { get; set; } = null!;
}
