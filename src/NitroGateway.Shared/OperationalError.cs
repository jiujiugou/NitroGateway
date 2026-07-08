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

    /// <summary>错误严重性</summary>
    public OperationalSeverity Severity { get; init; }
    /// <summary>错误分类</summary>
    public ErrorCategory Category { get; init; } = ErrorCategory.General;
    public override string ToString() => $"[{Code}] {Message}";
    /// <summary>
    /// 创建一个超时错误
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public static OperationalError Timeout(string message)
    {
        return new()
        {
            Category = ErrorCategory.Communication,
            Code = "Timeout",
            Severity = OperationalSeverity.Warning,
            Message = message
        };
    }
    /// <summary>
    /// 创建一个协议错误
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public static OperationalError Protocol(string message)
    {
        return new()
        {
            Category = ErrorCategory.Protocol,
            Code = "ProtocolError",
            Severity = OperationalSeverity.Warning,
            Message = message
        };
    }
    /// <summary>创建一个通用错误</summary>
    public static OperationalError General(string message)
    {
        return new()
        {
            Category = ErrorCategory.General,
            Code = "GeneralError",
            Severity = OperationalSeverity.Warning,
            Message = message
        };
    }

    /// <summary>创建一个参数校验错误</summary>
    public static OperationalError Validation(string message)
    {
        return new()
        {
            Category = ErrorCategory.Validation,
            Code = "ValidationError",
            Severity = OperationalSeverity.Warning,
            Message = message
        };
    }

    /// <summary>创建一个资源不可用错误</summary>
    public static OperationalError Unavailable(string message)
    {
        return new()
        {
            Category = ErrorCategory.Resource,
            Code = "ResourceUnavailable",
            Severity = OperationalSeverity.Error,
            Message = message
        };
    }

    /// <summary>创建一个资源不存在错误</summary>
    public static OperationalError NotFound(string message)
    {
        return new()
        {
            Category = ErrorCategory.General,
            Code = "NotFound",
            Severity = OperationalSeverity.Info,
            Message = message
        };
    }

    /// <summary>创建一个存储空间不足错误</summary>
    public static OperationalError StorageFull(string message)
    {
        return new()
        {
            Category = ErrorCategory.Storage,
            Code = "DiskFull",
            Severity = OperationalSeverity.Critical,
            Message = message
        };
    }

    /// <summary>创建一个数据库被锁定错误</summary>
    public static OperationalError DatabaseLocked(string message)
    {
        return new()
        {
            Category = ErrorCategory.Storage,
            Code = "DatabaseLocked",
            Severity = OperationalSeverity.Error,
            Message = message
        };
    }

    /// <summary>创建一个通用存储错误</summary>
    public static OperationalError Storage(string message)
    {
        return new()
        {
            Category = ErrorCategory.Storage,
            Code = "StorageError",
            Severity = OperationalSeverity.Error,
            Message = message
        };
    }
}
public enum OperationalSeverity
{
    /// <summary>信息性错误（不影响采集流程）</summary>
    Info,

    /// <summary>警告性错误（可能影响采集流程）</summary>
    Warning,

    /// <summary>严重错误（会导致采集流程中断）</summary>
    Error,
    /// <summary>
    /// 致命错误（会导致采集引擎崩溃）
    /// </summary>
    Critical
}
public enum ErrorCategory
{
    Communication, // 通信
    Storage,       // 存储
    Protocol,      // 协议
    Validation,    // 参数
    Resource,      // 系统资源
    General
}
