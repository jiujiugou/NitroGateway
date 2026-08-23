using NitroGateway.Shared;

namespace NitroGateway.DeviceManagement;

/// <summary>写请求 DTO（docs/14）。Web 写端点与桌面写值共用同一链路；<see cref="Value"/> 为原始用户输入。</summary>
public sealed record WriteRequest
{
    /// <summary>目标设备 ID</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>目标点位 ID</summary>
    public required Guid PointId { get; init; }

    /// <summary>
    /// 写入值（用户输入）。来源不同形态不同：Web 为 <see cref="System.Text.Json.JsonElement"/>，
    /// 桌面为字符串（WriteValueEditor.InputValue）。服务内按点位 <c>DevicePoint.DataType</c> 统一转换。
    /// </summary>
    public required object Value { get; init; }
}

/// <summary>
/// 写服务：校验（Access + WriteGuard 三级门控）→ 驱动池取长连接 → 确保连接 → WriteAsync。
/// 校验失败/驱动失败统一返回 <see cref="OperationResult"/>，不抛异常。
/// </summary>
public interface IWriteService
{
    /// <summary>校验并下发单点写命令。</summary>
    Task<OperationResult> WriteAsync(WriteRequest request, CancellationToken ct = default);
}
