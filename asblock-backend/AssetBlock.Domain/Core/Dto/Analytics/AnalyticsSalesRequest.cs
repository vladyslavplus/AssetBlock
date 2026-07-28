using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsSalesRequest(
    DateOnly From,
    DateOnly To,
    AnalyticsProductTypeFilter ProductType = AnalyticsProductTypeFilter.ALL,
    string? Cursor = null,
    int PageSize = AnalyticsConstants.DEFAULT_SALES_PAGE_SIZE);
