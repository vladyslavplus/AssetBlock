using AssetBlock.Application.Common.Validators;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

internal sealed class UpdateAssetCommandValidator : AbstractValidator<UpdateAssetCommand>
{
    public UpdateAssetCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => c.Title is not null || c.Description is not null || c.Price.HasValue || c.CategoryId.HasValue)
            .WithMessage("At least one field (Title, Description, Price, CategoryId) must be provided.");

        RuleFor(c => c.Title)
            .NotEmpty().WithMessage("Title cannot be empty when provided.")
            .MaximumLength(ListingSuggestionBounds.TITLE_MAX_LENGTH)
            .WithMessage($"Title must not exceed {ListingSuggestionBounds.TITLE_MAX_LENGTH} characters.")
            .When(c => c.Title is not null);

        RuleFor(c => c.Description)
            .MaximumLength(ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH)
            .WithMessage($"Description must not exceed {ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH} characters.")
            .When(c => c.Description is not null);

        When(c => c.Price.HasValue, () =>
        {
            RuleFor(c => c.Price!.Value).MarketplacePrice();
        });
    }
}
