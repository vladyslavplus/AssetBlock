using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.RefreshToken;

internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(2000).WithMessage("Refresh token must not exceed 2000 characters.");
    }
}
