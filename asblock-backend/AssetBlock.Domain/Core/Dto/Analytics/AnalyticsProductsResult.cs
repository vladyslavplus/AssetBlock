namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsProductsResult(
    DateOnly From,
    DateOnly To,
    string Timezone,
    string Currency,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AnalyticsProductItem> Items,
    int TotalCount,
    int Page,
    int PageSize);
