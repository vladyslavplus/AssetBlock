using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDraft;

internal sealed class SaveAssetDraftCommandValidator : AbstractValidator<SaveAssetDraftCommand>
{
    public SaveAssetDraftCommandValidator()
    {
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
        RuleFor(c => c.ExpectedWorkspaceRevision).GreaterThan(0);

        // Material and its fields must be non-null before the handler trims them; a malformed
        // direct backend request gets a validation result, not a NullReferenceException.
        RuleFor(c => c.Material)
            .NotNull()
            .WithMessage("Material payload is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Material!.Title)
                    .NotNull()
                    .NotEmpty()
                    .MaximumLength(SellerDraftLimits.TITLE_MAX_LENGTH)
                    .WithMessage($"Title must be between 1 and {SellerDraftLimits.TITLE_MAX_LENGTH} characters.");

                RuleFor(c => c.Material!.Description)
                    .MaximumLength(SellerDraftLimits.DESCRIPTION_MAX_LENGTH)
                    .When(c => c.Material!.Description is not null);

                RuleFor(c => c.Material!.CategoryId).NotEmpty();

                RuleFor(c => c.Material!.Tags)
                    .Must(tags => tags is null || (tags.All(t => t is not null) && tags.Count <= SellerDraftLimits.COMPONENTS_MAX_COUNT))
                    .WithMessage($"Tags must contain at most {SellerDraftLimits.COMPONENTS_MAX_COUNT} non-null entries.");
            });
    }
}
