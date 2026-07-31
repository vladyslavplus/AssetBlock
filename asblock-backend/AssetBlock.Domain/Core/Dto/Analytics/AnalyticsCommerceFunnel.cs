namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Durable checkout-intent funnel for the selected period.
/// </summary>
public sealed record AnalyticsCommerceFunnel(
    int CheckoutStarts,
    int StripeSessionsAttached,
    int CompletedOrders,
    int CancelledCheckouts,
    int PendingCheckouts,
    decimal? CheckoutCompletionRate,
    decimal? TerminalAbandonmentRate);
