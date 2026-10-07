using AssetBlock.Application.Common.Validators;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.UpdateAssetPrice;

internal sealed class UpdateAssetPriceCommandValidator : AbstractValidator<UpdateAssetPriceCommand>
{
    public UpdateAssetPriceCommandValidator()
    {
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.Price).MarketplacePrice();
    }
}
