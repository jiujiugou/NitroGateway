using FluentValidation;

namespace NitroGateway.Security.Guard;

/// <summary>变化率校验：不允许瞬间跳变超过 100%</summary>
public sealed class RateLimitValidator : AbstractValidator<WriteCommand>
{
    public RateLimitValidator()
    {
        When(cmd => cmd.PreviousValue.HasValue, () =>
        {
            RuleFor(cmd => cmd.Value)
                .Must((cmd, value) =>
                {
                    var prev = cmd.PreviousValue!.Value;
                    if (Math.Abs(prev) < 0.001) return true; // 上次值为 0，不校验
                    var change = Math.Abs((value - prev) / prev);
                    return change <= 1.0; // 变化不超过 100%
                })
                .WithMessage("值变化率超限");
        });
    }
}
