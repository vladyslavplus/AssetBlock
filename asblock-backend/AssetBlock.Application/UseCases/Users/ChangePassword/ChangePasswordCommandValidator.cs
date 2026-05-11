using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.ChangePassword;

internal sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(c => c.CurrentPassword)
            .NotEmpty()
            .WithMessage("Current password is required.");
        RuleFor(c => c.NewPassword)
            .NotEmpty()
            .WithMessage("New password is required.")
            .MinimumLength(8)
            .WithMessage("New password must be at least 8 characters.")
            .MaximumLength(500)
            .WithMessage("New password must not exceed 500 characters.")
            .NotEqual(c => c.CurrentPassword)
            .WithMessage("New password must be different from the current password.");
    }
}
