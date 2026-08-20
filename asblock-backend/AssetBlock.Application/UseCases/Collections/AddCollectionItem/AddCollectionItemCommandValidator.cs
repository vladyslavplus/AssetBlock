using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.AddCollectionItem;

internal sealed class AddCollectionItemCommandValidator : AbstractValidator<AddCollectionItemCommand>
{
    public AddCollectionItemCommandValidator()
    {
        RuleFor(c => c.CollectionId)
            .NotEmpty().WithMessage("CollectionId is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(c => c.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");
    }
}
