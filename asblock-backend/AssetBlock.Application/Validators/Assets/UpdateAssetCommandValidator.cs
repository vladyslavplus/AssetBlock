using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

public sealed class UpdateAssetCommandValidator : AbstractValidator<UpdateAssetCommand>
{
    public UpdateAssetCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => c.Title is not null || c.Description is not null || c.Price.HasValue || c.CategoryId.HasValue)
            .WithMessage("At least one field (Title, Description, Price, CategoryId) must be provided.");
    }
}
