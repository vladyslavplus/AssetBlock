namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Raw commerce funnel counts for one period.</summary>
public sealed record AnalyticsCommerceFunnelRaw(
    int CheckoutStarts,
    int StripeSessionsAttached,
    int CompletedOrders,
    int CancelledCheckouts,
    int PendingCheckouts);
