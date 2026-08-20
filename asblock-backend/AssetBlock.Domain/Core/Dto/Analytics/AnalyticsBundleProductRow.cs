namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Raw bundle product stats as returned by the analytics store.
/// Bundle revenue = SUM(Order.AmountPaid), not sum of lines.
/// </summary>
public sealed record AnalyticsBundleProductRow(
    Guid BundleId,
    string Title,
    bool IsArchived,
    decimal GrossRevenue,
    int Orders,
    int UnitsSold,
    DateTimeOffset? LatestSaleAt,
    decimal? CurrentPrice,
    decimal? ListPriceTotal);
