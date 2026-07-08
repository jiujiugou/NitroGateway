using FluentValidation;

namespace NitroGateway.Security.Guard;

/// <summary>设备模式校验：Offline / Unknown 状态不可写入</summary>
public sealed class ModeValidator : AbstractValidator<WriteCommand>
{
    public ModeValidator()
    {
        RuleFor(cmd => cmd.DeviceStatus)
            .Must(status => status is "Online")
            .WithMessage("设备不在线，无法执行写操作");
    }
}
