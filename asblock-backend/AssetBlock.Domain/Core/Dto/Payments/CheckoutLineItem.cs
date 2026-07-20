namespace AssetBlock.Domain.Core.Dto.Payments;

/// <summary>Server-derived checkout item. Never accept version/price/title from the browser.</summary>
public sealed record CheckoutLineItem(
    Guid CheckoutIntentId,
    Guid AssetId,
    Guid AssetVersionId,
    string Title,
    decimal UnitAmount,
    string Currency);
