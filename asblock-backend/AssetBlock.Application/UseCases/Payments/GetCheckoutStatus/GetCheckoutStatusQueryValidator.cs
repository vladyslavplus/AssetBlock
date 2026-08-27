using FluentValidation;

namespace AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;

internal sealed class GetCheckoutStatusQueryValidator : AbstractValidator<GetCheckoutStatusQuery>
{
    public GetCheckoutStatusQueryValidator()
    {
        RuleFor(q => q.CheckoutIntentId)
            .NotEmpty().WithMessage("CheckoutIntentId is required.");

        RuleFor(q => q.UserId)
            .NotEmpty().WithMessage("UserId is required.");
    }
}
