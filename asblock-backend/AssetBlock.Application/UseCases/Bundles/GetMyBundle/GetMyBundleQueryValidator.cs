using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundle;

internal sealed class GetMyBundleQueryValidator : AbstractValidator<GetMyBundleQuery>
{
    public GetMyBundleQueryValidator()
    {
        RuleFor(q => q.BundleId)
            .NotEmpty().WithMessage("BundleId is required.");
        RuleFor(q => q.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
