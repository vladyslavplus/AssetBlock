using FluentValidation;

namespace AssetBlock.Application.UseCases.Auth.Login;

internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(c => c.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Invalid email format.")
            .MaximumLength(256).WithMessage("Email must not exceed 256 characters.");
        RuleFor(c => c.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MaximumLength(500).WithMessage("Password must not exceed 500 characters.");
    }
}
