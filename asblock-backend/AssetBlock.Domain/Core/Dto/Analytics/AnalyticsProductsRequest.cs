using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsProductsRequest(
    DateOnly From,
    DateOnly To,
    AnalyticsProductTypeFilter ProductType = AnalyticsProductTypeFilter.ALL,
    AnalyticsProductSort Sort = AnalyticsProductSort.REVENUE,
    AnalyticsSortDirection Direction = AnalyticsSortDirection.DESC,
    int Page = 1,
    int PageSize = 20);
