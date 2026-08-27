using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;

internal sealed class GetSellerAnalyticsProductsQueryValidator : AbstractValidator<GetSellerAnalyticsProductsQuery>
{
    public GetSellerAnalyticsProductsQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.Request.From,
            q => q.Request.To);

        RuleFor(q => q.Request.ProductType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": invalid productType.");

        RuleFor(q => q.Request.Sort)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": invalid sort.");

        RuleFor(q => q.Request.Direction)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": invalid direction.");

        RuleFor(q => q.Request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": 'page' must be >= 1.")
            .LessThanOrEqualTo(AnalyticsConstants.MAX_PRODUCTS_PAGE)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_FILTER +
                $": 'page' must not exceed {AnalyticsConstants.MAX_PRODUCTS_PAGE}.");

        RuleFor(q => q)
            .Must(q => (q.Request.Page - 1L) * q.Request.PageSize <= AnalyticsConstants.MAX_PRODUCTS_OFFSET)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_FILTER +
                $": page offset must not exceed {AnalyticsConstants.MAX_PRODUCTS_OFFSET}.");

        RuleFor(q => q.Request.PageSize)
            .InclusiveBetween(1, AnalyticsConstants.MAX_PRODUCTS_PAGE_SIZE)
            .WithMessage(
                $"'pageSize' must be between 1 and {AnalyticsConstants.MAX_PRODUCTS_PAGE_SIZE}.");

        // RATING sort is only valid for ASSET or ALL type (bundles have no rating)
        RuleFor(q => q)
            .Must(q => q.Request.Sort != AnalyticsProductSort.RATING ||
                       q.Request.ProductType != AnalyticsProductTypeFilter.BUNDLE)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": RATING sort is not supported for BUNDLE type.");
    }
}
