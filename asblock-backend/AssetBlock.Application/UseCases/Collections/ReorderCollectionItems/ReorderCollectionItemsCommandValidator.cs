using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.ReorderCollectionItems;

internal sealed class ReorderCollectionItemsCommandValidator : AbstractValidator<ReorderCollectionItemsCommand>
{
    public ReorderCollectionItemsCommandValidator()
    {
        RuleFor(c => c.CollectionId)
            .NotEmpty().WithMessage("CollectionId is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(c => c.AssetIds)
            .NotNull().WithMessage("AssetIds is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.AssetIds)
                    .Must(ids => ids.Count <= CollectionConstants.MAX_ITEMS)
                    .WithMessage($"AssetIds must not exceed {CollectionConstants.MAX_ITEMS} items.");

                RuleFor(c => c.AssetIds)
                    .Must(ids => ids.All(id => id != Guid.Empty))
                    .WithMessage("AssetIds must not contain empty Guids.");

                RuleFor(c => c.AssetIds)
                    .Must(ids => ids.Distinct().Count() == ids.Count)
                    .WithMessage("AssetIds must be distinct.");
            });
    }
}
