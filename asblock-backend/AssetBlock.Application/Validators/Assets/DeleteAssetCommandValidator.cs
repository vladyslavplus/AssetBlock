using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

internal sealed class DeleteAssetCommandValidator : AbstractValidator<DeleteAssetCommand>
{
    public DeleteAssetCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Asset ID is required.");
        RuleFor(x => x.UserId).NotEmpty().WithMessage("User ID is required.");
    }
}
