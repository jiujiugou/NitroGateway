using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace NitroGateway.Security.Guard;

/// <summary>
/// 写指令门控。组合 Range、RateLimit、Mode 三个校验器。
/// 全部通过才放行 WriteAsync。
/// </summary>
public sealed class WriteGuard
{
    private readonly RangeValidator _range;
    private readonly RateLimitValidator _rateLimit;
    private readonly ModeValidator _mode;
    private readonly ILogger<WriteGuard> _logger;

    public WriteGuard(
        RangeValidator range,
        RateLimitValidator rateLimit,
        ModeValidator mode,
        ILogger<WriteGuard> logger)
    {
        _range = range;
        _rateLimit = rateLimit;
        _mode = mode;
        _logger = logger;
    }

    /// <summary>三级校验，返回失败原因或成功</summary>
    public ValidationResult Evaluate(WriteCommand cmd)
    {
        var modeResult = _mode.Validate(cmd);
        if (!modeResult.IsValid)
        {
            _logger.LogWarning("写指令拒绝(设备状态): Device={DeviceId} Status={Status}",
                cmd.DeviceId, cmd.DeviceStatus);
            return modeResult;
        }

        var rangeResult = _range.Validate(cmd);
        if (!rangeResult.IsValid)
        {
            _logger.LogWarning("写指令拒绝(超出范围): Value={Value} [{Min},{Max}]",
                cmd.Value, cmd.MinLimit, cmd.MaxLimit);
            return rangeResult;
        }

        var rateResult = _rateLimit.Validate(cmd);
        if (!rateResult.IsValid)
        {
            _logger.LogWarning("写指令拒绝(变化率): Value={Value} Prev={Prev}",
                cmd.Value, cmd.PreviousValue);
            return rateResult;
        }

        _logger.LogInformation("写指令通过校验: Device={DeviceId} Point={PointId} Value={Value}",
            cmd.DeviceId, cmd.PointId, cmd.Value);
        return new ValidationResult();
    }
}
