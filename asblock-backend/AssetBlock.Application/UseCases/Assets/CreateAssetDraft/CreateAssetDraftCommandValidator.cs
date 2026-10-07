using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.CreateAssetDraft;

internal sealed class CreateAssetDraftCommandValidator : AbstractValidator<CreateAssetDraftCommand>
{
    public CreateAssetDraftCommandValidator()
    {
        RuleFor(c => c.Title)
            .NotEmpty()
            .MaximumLength(SellerDraftLimits.TITLE_MAX_LENGTH)
            .WithMessage($"Title must be between 1 and {SellerDraftLimits.TITLE_MAX_LENGTH} characters.");

        RuleFor(c => c.Description)
            .MaximumLength(SellerDraftLimits.DESCRIPTION_MAX_LENGTH)
            .When(c => c.Description is not null);

        RuleFor(c => c.Price).MarketplacePrice();

        RuleFor(c => c.CategoryId).NotEmpty();
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
    }
}
