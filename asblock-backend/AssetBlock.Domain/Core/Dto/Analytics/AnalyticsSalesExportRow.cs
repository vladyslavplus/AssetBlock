namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A single order row streamed for seller analytics CSV export.
/// No buyer or Stripe details are included.
/// </summary>
public sealed record AnalyticsSalesExportRow(
    DateTimeOffset PurchasedAt,
    Guid OrderId,
    string ProductType,
    Guid ProductId,
    string ProductTitle,
    int Units,
    decimal GrossRevenue);
