using FluentValidation;

namespace NitroGateway.Security.Guard;

/// <summary>值范围校验：Value 必须在 [MinLimit, MaxLimit] 内</summary>
public sealed class RangeValidator : AbstractValidator<WriteCommand>
{
    public RangeValidator()
    {
        When(cmd => cmd.MinLimit.HasValue || cmd.MaxLimit.HasValue, () =>
        {
            RuleFor(cmd => cmd.Value)
                .Must((cmd, value) =>
                {
                    if (cmd.MinLimit.HasValue && value < cmd.MinLimit.Value) return false;
                    if (cmd.MaxLimit.HasValue && value > cmd.MaxLimit.Value) return false;
                    return true;
                })
                .WithMessage("值超出允许范围");
        });
    }
}
