using FluentValidation;

namespace AssetBlock.Application.UseCases.Payments.CreateBundleCheckoutSession;

internal sealed class CreateBundleCheckoutSessionCommandValidator
    : AbstractValidator<CreateBundleCheckoutSessionCommand>
{
    public CreateBundleCheckoutSessionCommandValidator()
    {
        RuleFor(c => c.BundleId)
            .NotEmpty().WithMessage("BundleId is required.");

        RuleFor(c => c.UserId)
            .NotEmpty().WithMessage("UserId is required.");
    }
}
