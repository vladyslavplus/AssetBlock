using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.Logout;

internal sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(c => c.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(2000).WithMessage("Refresh token must not exceed 2000 characters.");
    }
}
