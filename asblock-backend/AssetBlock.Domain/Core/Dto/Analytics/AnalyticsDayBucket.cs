namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Raw per-day aggregate bucket for a seller, as returned by the analytics store.
/// </summary>
public sealed record AnalyticsDayBucket(
    DateOnly Date,
    decimal GrossRevenue,
    int Orders,
    int Units);
