using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.CreateBundle;

internal sealed class CreateBundleCommandValidator : AbstractValidator<CreateBundleCommand>
{
    public CreateBundleCommandValidator()
    {
        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(c => c.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(BundleConstants.TITLE_MAX_LENGTH)
            .WithMessage($"Title must not exceed {BundleConstants.TITLE_MAX_LENGTH} characters.");

        RuleFor(c => c.Description)
            .MaximumLength(BundleConstants.DESCRIPTION_MAX_LENGTH)
            .WithMessage($"Description must not exceed {BundleConstants.DESCRIPTION_MAX_LENGTH} characters.")
            .When(c => c.Description is not null);

        RuleFor(c => c.Price).MarketplacePrice();

        RuleFor(c => c.AssetIds)
            .NotNull().WithMessage("AssetIds is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.AssetIds)
                    .Must(ids => ids.Count is >= BundleConstants.MIN_ITEMS and <= BundleConstants.MAX_ITEMS)
                    .WithMessage(ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BUNDLE_ASSET_COUNT_INVALID));

                RuleFor(c => c.AssetIds)
                    .Must(ids => ids.Distinct().Count() == ids.Count)
                    .WithMessage(ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BUNDLE_DUPLICATE_ASSET));

                RuleForEach(c => c.AssetIds)
                    .NotEmpty().WithMessage("Asset id is required.");
            });
    }
}
