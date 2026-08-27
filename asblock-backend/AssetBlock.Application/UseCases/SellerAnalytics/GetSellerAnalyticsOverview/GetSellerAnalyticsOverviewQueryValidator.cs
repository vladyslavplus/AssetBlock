using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

internal sealed class GetSellerAnalyticsOverviewQueryValidator : AbstractValidator<GetSellerAnalyticsOverviewQuery>
{
    public GetSellerAnalyticsOverviewQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.From,
            q => q.To);
    }
}
