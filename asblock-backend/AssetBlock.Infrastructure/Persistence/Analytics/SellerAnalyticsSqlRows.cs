namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal sealed class OverviewFactsAndCommerceContextSqlRow
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
    public double? AverageRating { get; set; }
    public int CurrentNewReviews { get; set; }
    public int ComparisonNewReviews { get; set; }
    public DateTimeOffset? EngagementAvailableFrom { get; set; }
    public int CheckoutStarts { get; set; }
    public int StripeSessionsAttached { get; set; }
    public int CompletedOrders { get; set; }
    public int CancelledCheckouts { get; set; }
    public int PendingCheckouts { get; set; }
    public decimal? TrackedCheckoutCoverage { get; set; }
}

internal sealed class TopProductsUnionSqlRow
{
    public string ProductKind { get; set; } = "";
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
}

internal sealed class CommerceContextSqlRow
{
    public DateTimeOffset? EngagementAvailableFrom { get; set; }
    public int CheckoutStarts { get; set; }
    public int StripeSessionsAttached { get; set; }
    public int CompletedOrders { get; set; }
    public int CancelledCheckouts { get; set; }
    public int PendingCheckouts { get; set; }
    public decimal? TrackedCheckoutCoverage { get; set; }
}

internal sealed class EngagementMetricsDualSqlRow
{
    public long CurrentProductViews { get; set; }
    public long CurrentUniqueVisitors { get; set; }
    public long CurrentDownloadRequests { get; set; }
    public long CurrentCollectionViews { get; set; }
    public long CurrentCollectionItemClicks { get; set; }
    public long ComparisonProductViews { get; set; }
    public long ComparisonUniqueVisitors { get; set; }
    public long ComparisonDownloadRequests { get; set; }
    public long ComparisonCollectionViews { get; set; }
    public long ComparisonCollectionItemClicks { get; set; }
    public int ViewSessions { get; set; }
    public int CheckoutSessions { get; set; }
    public int CompletedSessions { get; set; }
}

internal sealed class EngagementMetricsCurrentSqlRow
{
    public long CurrentProductViews { get; set; }
    public long CurrentUniqueVisitors { get; set; }
    public long CurrentDownloadRequests { get; set; }
    public long CurrentCollectionViews { get; set; }
    public long CurrentCollectionItemClicks { get; set; }
    public int ViewSessions { get; set; }
    public int CheckoutSessions { get; set; }
    public int CompletedSessions { get; set; }
}

internal sealed class TrafficUnionSqlRow
{
    public string RowKind { get; set; } = "";
    public string Key { get; set; } = "";
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public int CheckoutStarts { get; set; }
    public int CompletedOrders { get; set; }
    public decimal AttributedGrossRevenue { get; set; }
}

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

internal sealed class AnalyticsSaleExportSqlRow
{
    public long PeekCount { get; set; }
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

internal sealed class ScalarBoolSqlRow
{
    public bool Value { get; set; }
}

internal sealed class ScalarDateTimeOffsetSqlRow
{
    public DateTimeOffset? Value { get; set; }
}

internal sealed class ScalarDecimalSqlRow
{
    public decimal? Value { get; set; }
}

internal sealed class EngagementFactsSqlRow
{
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public long DownloadRequests { get; set; }
    public long CollectionViews { get; set; }
    public long CollectionItemClicks { get; set; }
}

internal sealed class DualPeriodEngagementFactsSqlRow
{
    public long CurrentProductViews { get; set; }
    public long CurrentUniqueVisitors { get; set; }
    public long CurrentDownloadRequests { get; set; }
    public long CurrentCollectionViews { get; set; }
    public long CurrentCollectionItemClicks { get; set; }
    public long ComparisonProductViews { get; set; }
    public long ComparisonUniqueVisitors { get; set; }
    public long ComparisonDownloadRequests { get; set; }
    public long ComparisonCollectionViews { get; set; }
    public long ComparisonCollectionItemClicks { get; set; }
}

internal sealed class EngagementEventDaySeriesSqlRow
{
    public DateOnly DayUtc { get; set; }
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public long DownloadRequests { get; set; }
}

internal sealed class EngagementCheckoutDaySeriesSqlRow
{
    public DateOnly DayUtc { get; set; }
    public int CheckoutStarts { get; set; }
    public int CompletedOrders { get; set; }
}

internal sealed class CommerceFunnelSqlRow
{
    public int CheckoutStarts { get; set; }
    public int StripeSessionsAttached { get; set; }
    public int CompletedOrders { get; set; }
    public int CancelledCheckouts { get; set; }
    public int PendingCheckouts { get; set; }
}

internal sealed class TrackedFunnelSqlRow
{
    public int ViewSessions { get; set; }
    public int CheckoutSessions { get; set; }
    public int CompletedSessions { get; set; }
}

internal sealed class AssetDetailHeaderSqlRow
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

internal sealed class BundleDetailHeaderSqlRow
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

internal sealed class ProductEngagementTotalsSqlRow
{
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public long DownloadRequests { get; set; }
}

internal sealed class BundleEngagementTotalsSqlRow
{
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
}

internal sealed class EngagementDaySeriesSqlRow
{
    public DateOnly DayUtc { get; set; }
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public int CheckoutStarts { get; set; }
    public int CompletedOrders { get; set; }
    public long DownloadRequests { get; set; }
}

internal sealed class CollectionPageSqlRow
{
    public Guid CollectionId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public long Views { get; set; }
    public long UniqueVisitors { get; set; }
    public long ItemClicks { get; set; }
    public int AttributedCheckoutStarts { get; set; }
    public int AttributedCompletedOrders { get; set; }
    public decimal AttributedGrossRevenue { get; set; }
    public DateTimeOffset RecentAt { get; set; }
    public int TotalCount { get; set; }
}

internal sealed class CollectionTopAssetSqlRow
{
    public Guid CollectionId { get; set; }
    public Guid AssetId { get; set; }
    public string Title { get; set; } = "";
    public long Clicks { get; set; }
}
