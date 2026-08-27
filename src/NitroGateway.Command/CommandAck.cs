namespace NitroGateway.Command;

/// <summary>
/// 命令执行结果（与云侧契约一致：回执 result 字段 Success/Failure）。
/// 云侧 CommandAckParser 用 <c>Enum.TryParse</c> 还原，字符串名必须与枚举名一致。
/// </summary>
public enum CommandResult
{
    /// <summary>写值成功</summary>
    Success,

    /// <summary>写值失败（error 携带原因）</summary>
    Failure
}

/// <summary>
/// 命令回执（网关 → 云，commands/ack 载荷字段 commandId/result/error/at）。
/// 幂等缓存的最小单位：重复投递直接重发本回执，不重复写值。
/// </summary>
public sealed record CommandAck(CommandResult Result, string Error, DateTimeOffset At)
{
    /// <summary>成功回执（error 恒为空串，契约要求）</summary>
    public static CommandAck Success(DateTimeOffset at) => new(CommandResult.Success, "", at);

    /// <summary>失败回执（error 必填原因）</summary>
    public static CommandAck Failure(string error, DateTimeOffset at) => new(CommandResult.Failure, error ?? "", at);
}
