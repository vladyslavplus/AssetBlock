using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.GetBundle;

internal sealed class GetBundleQueryValidator : AbstractValidator<GetBundleQuery>
{
    public GetBundleQueryValidator()
    {
        RuleFor(q => q.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
