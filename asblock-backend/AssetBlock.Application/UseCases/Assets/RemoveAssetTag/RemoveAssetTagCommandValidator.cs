using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.RemoveAssetTag;

internal sealed class RemoveAssetTagCommandValidator : AbstractValidator<RemoveAssetTagCommand>
{
    public RemoveAssetTagCommandValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty().WithMessage("Asset ID is required.");
        RuleFor(x => x.UserId).NotEmpty().WithMessage("User ID is required.");
        RuleFor(x => x.TagId).NotEmpty().WithMessage("Tag ID is required.");
    }
}
