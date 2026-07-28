namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Raw aggregated facts for a seller for a given time period, returned by the analytics store.
/// All monetary values are in decimal (db precision); the handler converts to cents.
/// </summary>
public sealed record SellerAnalyticsRawFacts(
    decimal GrossRevenue,
    int Orders,
    int Units,
    decimal DirectRevenue,
    decimal BundleRevenue,
    int UniqueCustomers,
    int NewCustomers,
    int RepeatCustomers);
