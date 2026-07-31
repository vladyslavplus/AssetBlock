using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsAssetDetail;

internal sealed class GetSellerAnalyticsAssetDetailQueryValidator
    : AbstractValidator<GetSellerAnalyticsAssetDetailQuery>
{
    public GetSellerAnalyticsAssetDetailQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.From,
            q => q.To);
    }
}
