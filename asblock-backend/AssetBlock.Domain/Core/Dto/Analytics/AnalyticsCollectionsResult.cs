namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsCollectionsResult(
    DateOnly From,
    DateOnly To,
    string Timezone,
    string Currency,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? EngagementAvailableFrom,
    IReadOnlyList<AnalyticsCollectionItem> Items,
    int TotalCount,
    int Page,
    int PageSize);
