using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.RequestEmailChange;

internal sealed class RequestEmailChangeCommandValidator : AbstractValidator<RequestEmailChangeCommand>
{
    public RequestEmailChangeCommandValidator()
    {
        RuleFor(c => c.CurrentPassword)
            .NotEmpty().WithMessage("Current password is required.");
        RuleFor(c => c.NewEmail)
            .NotEmpty().WithMessage("New email is required.")
            .EmailAddress().WithMessage("New email must be a valid email address.")
            .MaximumLength(256).WithMessage("New email must not exceed 256 characters.");
    }
}
