using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;

internal sealed class GetSellerAnalyticsBundleDetailQueryValidator
    : AbstractValidator<GetSellerAnalyticsBundleDetailQuery>
{
    public GetSellerAnalyticsBundleDetailQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.From,
            q => q.To);
    }
}
