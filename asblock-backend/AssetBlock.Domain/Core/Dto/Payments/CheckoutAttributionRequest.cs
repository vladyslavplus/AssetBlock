using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Payments;

/// <summary>
/// Client hint about where a checkout was started from. Entirely best-effort: an inconsistent or
/// unverifiable combination is dropped server-side and never fails the checkout.
/// </summary>
public sealed record CheckoutAttributionRequest(
    AnalyticsTrafficSource? Source,
    Guid? CollectionId,
    string? ReferrerHost);
