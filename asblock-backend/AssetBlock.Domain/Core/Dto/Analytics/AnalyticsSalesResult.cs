namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsSalesResult(
    DateOnly From,
    DateOnly To,
    string Timezone,
    string Currency,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AnalyticsSaleItem> Items,
    bool HasMore,
    string? NextCursor);
