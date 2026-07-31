namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Session-intersection tracked funnel. Counts are monotonic: views >= checkout >= completed.
/// </summary>
public sealed record AnalyticsTrackedFunnel(
    int ViewSessions,
    int CheckoutSessions,
    int CompletedSessions,
    decimal? ViewToCheckoutRate,
    decimal? CheckoutToCompletedRate,
    decimal? ViewToCompletedRate);
