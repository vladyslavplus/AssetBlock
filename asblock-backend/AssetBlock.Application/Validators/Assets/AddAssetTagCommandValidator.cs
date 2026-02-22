using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using FluentValidation;

namespace AssetBlock.Application.Validators.Assets;

internal sealed class AddAssetTagCommandValidator : AbstractValidator<AddAssetTagCommand>
{
    public AddAssetTagCommandValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty().WithMessage("Asset ID is required.");
        RuleFor(x => x.UserId).NotEmpty().WithMessage("User ID is required.");
        RuleFor(x => x.TagName)
            .NotEmpty().WithMessage("Tag name cannot be empty.")
            .MaximumLength(50).WithMessage("Tag name maximum length is 50 characters.")
            .Matches("^[a-z0-9]+(-[a-z0-9]+)*$").WithMessage("Tag name must start and end with lowercase alphanumeric characters, with single hyphens between segments.");
    }
}
