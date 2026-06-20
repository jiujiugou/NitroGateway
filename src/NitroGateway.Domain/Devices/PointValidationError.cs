namespace NitroGateway.Domain.Devices;

/// <summary>点位校验错误</summary>
public sealed record PointValidationError
{
    /// <summary>字段名</summary>
    public required string Field { get; init; }

    /// <summary>错误描述</summary>
    public required string Message { get; init; }
}
