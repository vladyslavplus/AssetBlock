using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;

internal sealed class ConfirmEmailVerificationCommandValidator : AbstractValidator<ConfirmEmailVerificationCommand>
{
    public ConfirmEmailVerificationCommandValidator()
    {
        RuleFor(c => c.ProtectedToken)
            .NotEmpty().WithMessage("Token is required.")
            .MaximumLength(EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH)
            .WithMessage($"Token must not exceed {EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH} characters.");
    }
}
