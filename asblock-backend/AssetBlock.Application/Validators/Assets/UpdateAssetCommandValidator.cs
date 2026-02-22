using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

internal sealed class UpdateAssetCommandValidator : AbstractValidator<UpdateAssetCommand>
{
    private const int TITLE_MAX_LENGTH = 500;
    private const int DESCRIPTION_MAX_LENGTH = 2000;

    public UpdateAssetCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => c.Title is not null || c.Description is not null || c.Price.HasValue || c.CategoryId.HasValue)
            .WithMessage("At least one field (Title, Description, Price, CategoryId) must be provided.");

        RuleFor(c => c.Title)
            .NotEmpty().WithMessage("Title cannot be empty when provided.")
            .MaximumLength(TITLE_MAX_LENGTH).WithMessage("Title must not exceed 500 characters.")
            .When(c => c.Title is not null);

        RuleFor(c => c.Description)
            .MaximumLength(DESCRIPTION_MAX_LENGTH).WithMessage("Description must not exceed 2000 characters.")
            .When(c => c.Description is not null);

        RuleFor(c => c.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero.")
            .When(c => c.Price.HasValue);
    }
}
