using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;

internal sealed class GetSellerAnalyticsCollectionsQueryValidator
    : AbstractValidator<GetSellerAnalyticsCollectionsQuery>
{
    public GetSellerAnalyticsCollectionsQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.Request.From,
            q => q.Request.To);

        RuleFor(q => q.Request.Sort)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": invalid sort.");

        RuleFor(q => q.Request.Direction)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": invalid direction.");

        RuleFor(q => q.Request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": 'page' must be >= 1.")
            .LessThanOrEqualTo(AnalyticsConstants.MAX_COLLECTIONS_PAGE)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_FILTER +
                $": 'page' must not exceed {AnalyticsConstants.MAX_COLLECTIONS_PAGE}.");

        RuleFor(q => q)
            .Must(q => (q.Request.Page - 1L) * q.Request.PageSize <= AnalyticsConstants.MAX_COLLECTIONS_OFFSET)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_FILTER +
                $": page offset must not exceed {AnalyticsConstants.MAX_COLLECTIONS_OFFSET}.");

        RuleFor(q => q.Request.PageSize)
            .InclusiveBetween(1, AnalyticsConstants.MAX_COLLECTIONS_PAGE_SIZE)
            .WithMessage(
                $"'pageSize' must be between 1 and {AnalyticsConstants.MAX_COLLECTIONS_PAGE_SIZE}.");
    }
}
