namespace NitroGateway.Shared;

/// <summary>
/// 操作错误，携带错误码、消息和可选的附加信息。
/// 用于替代异常在调用链中传递故障信息，避免在采集热路径中抛出异常。
/// </summary>
public sealed class OperationalError
{
    /// <summary>错误码，如 "Modbus.Timeout"、"Buffer.QueueFull"</summary>
    public required string Code { get; init; }

    /// <summary>人类可读的错误描述</summary>
    public required string Message { get; init; }

    /// <summary>附加信息（如超时毫秒数、失败的设备 ID 等）</summary>
    public Dictionary<string, object>? Details { get; init; }

    public override string ToString() => $"[{Code}] {Message}";

    // ---- 预置常见错误 ----

    /// <summary>创建通用错误</summary>
    public static OperationalError General(string message) => new()
    {
        Code = "General.Error",
        Message = message
    };

    /// <summary>通信超时</summary>
    public static OperationalError Timeout(string message) => new()
    {
        Code = "Communication.Timeout",
        Message = message
    };

    /// <summary>协议层错误（校验失败、帧格式错误等）</summary>
    public static OperationalError Protocol(string message) => new()
    {
        Code = "Protocol.Error",
        Message = message
    };

    /// <summary>参数校验失败</summary>
    public static OperationalError Validation(string message) => new()
    {
        Code = "Validation.Error",
        Message = message
    };

    /// <summary>资源不可用（队列满、连接池耗尽等）</summary>
    public static OperationalError Unavailable(string message) => new()
    {
        Code = "Resource.Unavailable",
        Message = message
    };
}
