using AssetBlock.Domain.Core.Payments;
using FluentValidation;

namespace AssetBlock.Application.Common.Validators;

/// <summary>Shared FluentValidation rules for marketplace USD prices.</summary>
internal static class MarketplacePriceRules
{
    public static IRuleBuilderOptions<T, decimal> MarketplacePrice<T>(this IRuleBuilder<T, decimal> rule)
    {
        return rule
            .GreaterThan(0).WithMessage("Price must be greater than zero.")
            .LessThanOrEqualTo(BundlePriceAllocator.MAX_AMOUNT)
            .WithMessage($"Price must not exceed {BundlePriceAllocator.MAX_AMOUNT:F2}.")
            .Must(BundlePriceAllocator.HasAtMostTwoDecimalPlaces)
            .WithMessage("Price must have at most two decimal places.");
    }
}
