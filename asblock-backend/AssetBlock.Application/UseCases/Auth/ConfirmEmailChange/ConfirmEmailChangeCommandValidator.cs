using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailChange;

internal sealed class ConfirmEmailChangeCommandValidator : AbstractValidator<ConfirmEmailChangeCommand>
{
    public ConfirmEmailChangeCommandValidator()
    {
        RuleFor(c => c.ProtectedToken)
            .NotEmpty().WithMessage("Token is required.")
            .MaximumLength(EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH)
            .WithMessage($"Token must not exceed {EmailActionConstants.MAX_PROTECTED_TOKEN_LENGTH} characters.");
    }
}
