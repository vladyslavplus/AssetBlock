namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal sealed class DualPeriodFactsSqlRow
{
    public decimal CurrentGrossRevenue { get; set; }
    public int CurrentOrders { get; set; }
    public int CurrentUnits { get; set; }
    public decimal CurrentDirectRevenue { get; set; }
    public decimal CurrentBundleRevenue { get; set; }
    public int CurrentUniqueCustomers { get; set; }
    public int CurrentNewCustomers { get; set; }
    public int CurrentRepeatCustomers { get; set; }
    public decimal ComparisonGrossRevenue { get; set; }
    public int ComparisonOrders { get; set; }
    public int ComparisonUnits { get; set; }
    public decimal ComparisonDirectRevenue { get; set; }
    public decimal ComparisonBundleRevenue { get; set; }
    public int ComparisonUniqueCustomers { get; set; }
    public int ComparisonNewCustomers { get; set; }
    public int ComparisonRepeatCustomers { get; set; }
}

internal sealed class DaySeriesSqlRow
{
    public DateOnly SaleDate { get; set; }
    public decimal GrossRevenue { get; set; }
    public int Orders { get; set; }
    public int Units { get; set; }
}

internal sealed class TopAssetSqlRow
{
    public Guid AssetId { get; set; }
    public string Title { get; set; } = "";
    public bool IsDeleted { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal DirectRevenue { get; set; }
    public decimal BundleAllocatedRevenue { get; set; }
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public double? AverageRating { get; set; }
    public int ReviewCount { get; set; }
    public DateTimeOffset? LatestSaleAt { get; set; }
}

internal sealed class TopBundleSqlRow
{
    public Guid BundleId { get; set; }
    public string Title { get; set; } = "";
    public bool IsArchived { get; set; }
    public decimal GrossRevenue { get; set; }
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public DateTimeOffset? LatestSaleAt { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? ListPriceTotal { get; set; }
}

internal sealed class DualRatingsSqlRow
{
    public double? AverageRating { get; set; }
    public int CurrentNewReviews { get; set; }
    public int ComparisonNewReviews { get; set; }
}

internal sealed class AnalyticsProductSqlRow
{
    public int ProductKind { get; set; }
    public Guid ProductId { get; set; }
    public string Title { get; set; } = "";
    public bool IsDeletedOrArchived { get; set; }
    public decimal GrossRevenue { get; set; }
    public decimal DirectRevenue { get; set; }
    public decimal BundleAllocatedRevenue { get; set; }
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public double? AverageRating { get; set; }
    public int ReviewCount { get; set; }
    public DateTimeOffset? LatestSaleAt { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? ListPriceTotal { get; set; }
    public int TotalCount { get; set; }
}

internal sealed class AnalyticsSaleSqlRow
{
    public int ProductKind { get; set; }
    public Guid ProductId { get; set; }
    public string ProductTitle { get; set; } = "";
    public Guid OrderId { get; set; }
    public DateTimeOffset PurchasedAt { get; set; }
    public int Units { get; set; }
    public decimal GrossRevenue { get; set; }
}

internal sealed class ScalarIntSqlRow
{
    public int Value { get; set; }
}
