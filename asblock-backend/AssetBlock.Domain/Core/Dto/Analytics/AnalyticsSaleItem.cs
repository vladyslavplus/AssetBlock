using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A single row on the analytics sales feed. No buyer or Stripe details are exposed.
/// </summary>
public sealed record AnalyticsSaleItem(
    AnalyticsProductKind ProductKind,
    Guid ProductId,
    string ProductTitle,
    Guid OrderId,
    DateTimeOffset PurchasedAt,
    int Units,
    long GrossRevenueCents);
