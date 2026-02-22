using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

internal sealed class RemoveAssetTagCommandValidator : AbstractValidator<RemoveAssetTagCommand>
{
    public RemoveAssetTagCommandValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty().WithMessage("Asset ID is required.");
        RuleFor(x => x.UserId).NotEmpty().WithMessage("User ID is required.");
        RuleFor(x => x.TagId).NotEmpty().WithMessage("Tag ID is required.");
    }
}
