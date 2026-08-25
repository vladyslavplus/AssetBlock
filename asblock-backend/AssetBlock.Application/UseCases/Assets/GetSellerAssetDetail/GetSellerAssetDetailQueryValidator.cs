using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;

public sealed class GetSellerAssetDetailQueryValidator : AbstractValidator<GetSellerAssetDetailQuery>
{
    public GetSellerAssetDetailQueryValidator()
    {
        RuleFor(x => x.AssetId)
            .NotEmpty()
            .WithMessage("AssetId is required.");

        RuleFor(x => x.OwnerUserId)
            .NotEmpty()
            .WithMessage("OwnerUserId is required.");
    }
}
