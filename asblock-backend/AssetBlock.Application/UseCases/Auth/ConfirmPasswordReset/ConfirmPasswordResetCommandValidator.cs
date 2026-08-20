using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;

internal sealed class ConfirmPasswordResetCommandValidator : AbstractValidator<ConfirmPasswordResetCommand>
{
    public ConfirmPasswordResetCommandValidator()
    {
        RuleFor(c => c.ProtectedToken)
            .NotEmpty().WithMessage("Token is required.")
            .MaximumLength(EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH)
            .WithMessage($"Token must not exceed {EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH} characters.");
        RuleFor(c => c.NewPassword)
            .NotEmpty().WithMessage("New password is required.")
            .MinimumLength(8).WithMessage("New password must be at least 8 characters.")
            .MaximumLength(500).WithMessage("New password must not exceed 500 characters.");
    }
}
