using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsCollectionsRequest(
    DateOnly From,
    DateOnly To,
    AnalyticsCollectionSort Sort = AnalyticsCollectionSort.VIEWS,
    AnalyticsSortDirection Direction = AnalyticsSortDirection.DESC,
    int Page = 1,
    int PageSize = AnalyticsConstants.DEFAULT_COLLECTIONS_PAGE_SIZE);
