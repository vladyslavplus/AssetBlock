using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A single order row in the analytics sales feed as returned by the store.
/// No buyer or Stripe details are included.
/// </summary>
public sealed record AnalyticsSaleRow(
    AnalyticsProductKind ProductKind,
    Guid ProductId,
    string ProductTitle,
    Guid OrderId,
    DateTimeOffset PurchasedAt,
    int Units,
    decimal GrossRevenue);
