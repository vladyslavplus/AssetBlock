using FluentValidation;

namespace AssetBlock.Application.UseCases.Collections.RemoveCollectionItem;

internal sealed class RemoveCollectionItemCommandValidator : AbstractValidator<RemoveCollectionItemCommand>
{
    public RemoveCollectionItemCommandValidator()
    {
        RuleFor(c => c.CollectionId)
            .NotEmpty().WithMessage("CollectionId is required.");

        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        RuleFor(c => c.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");
    }
}
