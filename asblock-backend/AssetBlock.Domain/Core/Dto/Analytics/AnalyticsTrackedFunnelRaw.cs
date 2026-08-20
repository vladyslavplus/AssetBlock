namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Raw tracked funnel session counts for one period.</summary>
public sealed record AnalyticsTrackedFunnelRaw(
    int ViewSessions,
    int CheckoutSessions,
    int CompletedSessions);
