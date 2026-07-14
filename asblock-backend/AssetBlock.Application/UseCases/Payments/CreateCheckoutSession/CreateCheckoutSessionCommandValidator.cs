using FluentValidation;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandValidator : AbstractValidator<CreateCheckoutSessionCommand>
{
    public CreateCheckoutSessionCommandValidator()
    {
        RuleFor(c => c.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");

        RuleFor(c => c.UserId)
            .NotEmpty().WithMessage("UserId is required.");
    }
}
